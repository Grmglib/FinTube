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

    private static readonly Regex AgeRestrictionRegex = new(
        @"(Sign in to confirm your age|age-restricted|This video may be inappropriate for some users)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string StartTask(string executable, string args, ILogger logger, bool isPlaylist,
        string? retryArgs = null, Action? onCompleted = null)
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

        _ = Task.Run(() => RunDownload(taskId, executable, args, logger, retryArgs, onCompleted));

        return taskId;
    }

    private static async Task RunDownload(string taskId, string executable, string args, ILogger logger,
        string? retryArgs, Action? onCompleted)
    {
        if (!_tasks.TryGetValue(taskId, out var taskInfo))
            return;

        try
        {
            var (exitCode, collectedStderr) = await ExecuteYtdlp(executable, args, taskInfo);

            if (exitCode != 0 && !taskInfo.IsPlaylist && retryArgs != null && IsAgeRestricted(collectedStderr))
            {
                logger.LogWarning("Task {taskId}: age-restriction detected, retrying with cookies...", taskId);
                taskInfo.FailedCount = 0;
                taskInfo.FailedVideos.Clear();
                taskInfo.Progress = "";

                (exitCode, collectedStderr) = await ExecuteYtdlp(executable, retryArgs, taskInfo);
            }

            if (taskInfo.FailedCount > 0 && taskInfo.CompletedCount > 0)
            {
                taskInfo.Status = DownloadTaskStatus.CompletedWithErrors;
                taskInfo.ErrorMessage = $"{taskInfo.FailedCount} video(s) failed";
                logger.LogWarning("Task {taskId} completed with {failed} errors out of {total} videos", taskId, taskInfo.FailedCount, taskInfo.VideoCount);
            }
            else if (exitCode != 0 && taskInfo.CompletedCount == 0)
            {
                taskInfo.Status = DownloadTaskStatus.Failed;
                taskInfo.ErrorMessage = taskInfo.FailedVideos.Count > 0
                    ? string.Join("; ", taskInfo.FailedVideos)
                    : $"yt-dlp exited with code {exitCode}";
                logger.LogError("Task {taskId} failed: exit code {code}", taskId, exitCode);
            }
            else
            {
                taskInfo.Status = DownloadTaskStatus.Completed;
                if (taskInfo.IsPlaylist && taskInfo.VideoCount > 0)
                    taskInfo.CompletedCount = taskInfo.VideoCount - taskInfo.FailedCount;
                else if (!taskInfo.IsPlaylist)
                {
                    taskInfo.VideoCount = 1;
                    taskInfo.CompletedCount = 1;
                }
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

        if (taskInfo.Status == DownloadTaskStatus.Completed || taskInfo.Status == DownloadTaskStatus.CompletedWithErrors)
        {
            try
            {
                onCompleted?.Invoke();
                taskInfo.LibraryScanQueued = onCompleted != null;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Task {taskId}: onCompleted callback failed", taskId);
            }
        }
    }

    private static async Task<(int exitCode, string collectedStderr)> ExecuteYtdlp(
        string executable, string args, DownloadTaskInfo taskInfo)
    {
        var stderrLines = new List<string>();

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
            stderrLines.Add(e.Data);

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
            if (progressMatch.Success && progressMatch.Groups[1].Value == "100" && taskInfo.IsPlaylist)
            {
                taskInfo.CompletedCount++;
            }
        };

        process.Start();
        process.BeginErrorReadLine();

        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, string.Join("\n", stderrLines));
    }

    private static bool IsAgeRestricted(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return false;
        return AgeRestrictionRegex.IsMatch(stderr);
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
