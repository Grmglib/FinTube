using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTube.Models;

public static class DownloadTaskManager
{
    private static readonly ConcurrentDictionary<string, DownloadTaskInfo> _tasks = new();

    private static readonly Regex ProgressRegex = new(@"\[download\]\s+(\d+(?:\.\d+)?)%", RegexOptions.Compiled);
    private static readonly Regex PlaylistItemRegex = new(@"\[download\]\s+Downloading item (\d+) of (\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VideoOfRegex = new(@"\[download\]\s+Downloading video (\d+) of (\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ErrorRegex = new(@"^ERROR:\s*(.+)$", RegexOptions.Compiled);

    public static string StartTask(string executable, string args, ILogger logger, bool isPlaylist)
    {
        CleanupOldTasks();

        var taskId = Guid.NewGuid().ToString("N");
        var taskInfo = new DownloadTaskInfo
        {
            Id = taskId,
            Status = DownloadTaskStatus.Running,
            StartedAt = DateTime.UtcNow,
            IsPlaylist = isPlaylist
        };

        _tasks[taskId] = taskInfo;

        _ = Task.Run(() => RunDownload(taskId, executable, args, logger));

        return taskId;
    }

    private static async Task RunDownload(string taskId, string executable, string args, ILogger logger)
    {
        if (!_tasks.TryGetValue(taskId, out var taskInfo))
            return;

        try
        {
            var startInfo = new ProcessStartInfo()
            {
                FileName = executable,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process() { StartInfo = startInfo };
            process.EnableRaisingEvents = true;

            process.ErrorDataReceived += (sender, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;

                taskInfo.Progress = e.Data;

                var errorMatch = ErrorRegex.Match(e.Data);
                if (errorMatch.Success)
                {
                    taskInfo.FailedCount++;
                    taskInfo.FailedVideos.Add(errorMatch.Groups[1].Value);
                    return;
                }

                var playlistMatch = PlaylistItemRegex.Match(e.Data);
                if (!playlistMatch.Success)
                    playlistMatch = VideoOfRegex.Match(e.Data);

                if (playlistMatch.Success)
                {
                    if (int.TryParse(playlistMatch.Groups[1].Value, out var current))
                        taskInfo.CompletedCount = current - 1;
                    if (int.TryParse(playlistMatch.Groups[2].Value, out var total))
                        taskInfo.VideoCount = total;
                }

                var progressMatch = ProgressRegex.Match(e.Data);
                if (progressMatch.Success && progressMatch.Groups[1].Value == "100")
                {
                    if (taskInfo.IsPlaylist)
                        taskInfo.CompletedCount++;
                }
            };

            process.Start();
            process.BeginErrorReadLine();

            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (taskInfo.FailedCount > 0 && taskInfo.CompletedCount > 0)
            {
                taskInfo.Status = DownloadTaskStatus.CompletedWithErrors;
                taskInfo.ErrorMessage = $"{taskInfo.FailedCount} video(s) failed";
                logger.LogWarning("Task {taskId} completed with {failed} errors out of {total} videos", taskId, taskInfo.FailedCount, taskInfo.VideoCount);
            }
            else if (process.ExitCode != 0 && taskInfo.CompletedCount == 0)
            {
                taskInfo.Status = DownloadTaskStatus.Failed;
                taskInfo.ErrorMessage = taskInfo.FailedVideos.Count > 0
                    ? string.Join("; ", taskInfo.FailedVideos)
                    : $"yt-dlp exited with code {process.ExitCode}";
                logger.LogError("Task {taskId} failed: exit code {code}", taskId, process.ExitCode);
            }
            else
            {
                taskInfo.Status = DownloadTaskStatus.Completed;
                if (taskInfo.IsPlaylist && taskInfo.VideoCount > 0)
                    taskInfo.CompletedCount = taskInfo.VideoCount - taskInfo.FailedCount;
                logger.LogInformation("Task {taskId} completed successfully", taskId);
            }
        }
        catch (Exception ex)
        {
            taskInfo.Status = DownloadTaskStatus.Failed;
            taskInfo.ErrorMessage = ex.Message;
            logger.LogError(ex, "Task {taskId} failed with exception", taskId);
        }

        taskInfo.CompletedAt = DateTime.UtcNow;
    }

    public static DownloadTaskInfo? GetTask(string taskId)
    {
        return _tasks.TryGetValue(taskId, out var task) ? task : null;
    }

    public static List<DownloadTaskInfo> GetAllTasks()
    {
        return _tasks.Values.OrderByDescending(t => t.StartedAt).ToList();
    }

    private static void CleanupOldTasks()
    {
        var cutoff = DateTime.UtcNow.AddHours(-1);
        var oldKeys = _tasks
            .Where(kvp => kvp.Value.CompletedAt.HasValue && kvp.Value.CompletedAt.Value < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in oldKeys)
            _tasks.TryRemove(key, out _);
    }
}
