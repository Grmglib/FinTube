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

    private static readonly Regex AuthRequiredRegex = new(
        @"(Sign in to confirm your age|age-restricted|This video may be inappropriate for some users|Sign in to confirm you.re not a bot|Use --cookies-from-browser or --cookies)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string StartTask(string executable, string args, ILogger logger, bool isPlaylist,
        string? retryArgs = null, Action? onCompleted = null, MusicMetadata? musicMetadata = null,
        List<MusicMetadata?>? playlistMetadata = null, string? selectedItems = null)
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

        _ = Task.Run(() => RunDownload(taskId, executable, args, logger, retryArgs, onCompleted, musicMetadata, playlistMetadata, selectedItems));

        return taskId;
    }

    private static async Task RunDownload(string taskId, string executable, string args, ILogger logger,
        string? retryArgs, Action? onCompleted, MusicMetadata? musicMetadata,
        List<MusicMetadata?>? playlistMetadata, string? selectedItems = null)
    {
        if (!_tasks.TryGetValue(taskId, out var taskInfo))
            return;

        var finalStatus = DownloadTaskStatus.Failed;

        try
        {
            var (exitCode, collectedStderr, collectedStdout) = await ExecuteYtdlp(executable, args, taskInfo);

            if (exitCode != 0 && !taskInfo.IsPlaylist && retryArgs != null && IsAuthRequired(collectedStderr))
            {
                logger.LogWarning("Task {taskId}: YouTube authentication required, retrying with cookies...", taskId);
                taskInfo.FailedCount = 0;
                taskInfo.FailedVideos.Clear();
                taskInfo.Progress = "";

                (exitCode, collectedStderr, collectedStdout) = await ExecuteYtdlp(executable, retryArgs, taskInfo);
            }

            if (taskInfo.FailedCount > 0 && taskInfo.CompletedCount > 0)
            {
                finalStatus = DownloadTaskStatus.CompletedWithErrors;
                taskInfo.ErrorMessage = $"{taskInfo.FailedCount} video(s) failed";
                logger.LogWarning("Task {taskId} completed with {failed} errors out of {total} videos", taskId, taskInfo.FailedCount, taskInfo.VideoCount);
            }
            else if (exitCode != 0 && taskInfo.CompletedCount == 0)
            {
                finalStatus = DownloadTaskStatus.Failed;
                taskInfo.Status = DownloadTaskStatus.Failed;
                taskInfo.ErrorMessage = taskInfo.FailedVideos.Count > 0
                    ? string.Join("; ", taskInfo.FailedVideos)
                    : $"yt-dlp exited with code {exitCode}";
                logger.LogError("Task {taskId} failed: exit code {code}", taskId, exitCode);
            }
            else
            {
                finalStatus = DownloadTaskStatus.Completed;
                if (taskInfo.IsPlaylist && taskInfo.VideoCount > 0)
                    taskInfo.CompletedCount = taskInfo.VideoCount - taskInfo.FailedCount;
                else if (!taskInfo.IsPlaylist)
                {
                    taskInfo.VideoCount = 1;
                    taskInfo.CompletedCount = 1;
                }
                logger.LogInformation("Task {taskId} download completed successfully", taskId);
            }

            var outputPaths = ParseOutputFilePaths(collectedStdout);

            if ((finalStatus == DownloadTaskStatus.Completed || finalStatus == DownloadTaskStatus.CompletedWithErrors)
                && taskInfo.IsPlaylist && playlistMetadata != null && outputPaths.Count > 0)
            {
                var indexedPaths = ParseIndexedOutputPaths(collectedStdout);
                var processedCount = 0;
                var trackNum = 0;

                foreach (var (plIdx, filePath) in indexedPaths)
                {
                    trackNum++;
                    var metaIdx = MapMetadataIndex(plIdx, selectedItems, playlistMetadata.Count);
                    if (metaIdx < 0 || metaIdx >= playlistMetadata.Count)
                    {
                        logger.LogWarning("Task {taskId}: no metadata mapping for playlist_index {plIdx}", taskId, plIdx);
                        continue;
                    }
                    var meta = playlistMetadata[metaIdx];
                    if (meta == null || string.IsNullOrWhiteSpace(filePath))
                        continue;

                    try
                    {
                        taskInfo.Progress = $"Post-processing track {trackNum} of {indexedPaths.Count}...";
                        logger.LogInformation("Task {taskId}: post-processing track {idx} (playlist_index={plIdx}) on '{path}'", taskId, trackNum, plIdx, filePath);
                        await MusicPostProcessor.ProcessAsync(filePath, meta, logger);
                        try
                        {
                            MusicPostProcessor.OrganizeFile(filePath, meta, logger);
                        }
                        catch (Exception moveEx)
                        {
                            logger.LogWarning(moveEx, "Task {taskId}: failed to organize track {idx}", taskId, trackNum);
                        }
                        processedCount++;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Task {taskId}: post-processing failed for track {idx}", taskId, trackNum);
                    }
                }

                if (processedCount > 0)
                    taskInfo.PostProcessed = true;
                taskInfo.PostProcessedCount = processedCount;
            }
            else if ((finalStatus == DownloadTaskStatus.Completed || finalStatus == DownloadTaskStatus.CompletedWithErrors)
                && !taskInfo.IsPlaylist && musicMetadata != null && outputPaths.Count > 0)
            {
                taskInfo.OutputFilePath = outputPaths.Last();
                try
                {
                    taskInfo.Progress = "Post-processing metadata...";
                    logger.LogInformation("Task {taskId}: starting post-processing on '{path}'", taskId, taskInfo.OutputFilePath);
                    await MusicPostProcessor.ProcessAsync(taskInfo.OutputFilePath, musicMetadata, logger);
                    taskInfo.PostProcessed = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Task {taskId}: post-processing failed", taskId);
                }
            }

            if (finalStatus == DownloadTaskStatus.Completed || finalStatus == DownloadTaskStatus.CompletedWithErrors)
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
        catch (Exception ex)
        {
            finalStatus = DownloadTaskStatus.Failed;
            taskInfo.ErrorMessage = ex.Message;
            logger.LogError(ex, "Task {taskId} failed with exception", taskId);
        }

        taskInfo.Status = finalStatus;
        taskInfo.CompletedAt = DateTime.UtcNow;
    }

    private static List<string> ParseOutputFilePaths(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return new List<string>();
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().Trim('"'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
    }

    private static List<(int playlistIndex, string filePath)> ParseIndexedOutputPaths(string stdout)
    {
        var result = new List<(int, string)>();
        if (string.IsNullOrWhiteSpace(stdout)) return result;

        foreach (var rawLine in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var tabIdx = line.IndexOf('\t');
            if (tabIdx > 0 && int.TryParse(line[..tabIdx], out var plIdx))
                result.Add((plIdx, line[(tabIdx + 1)..].Trim('"')));
        }
        return result;
    }

    private static int MapMetadataIndex(int playlistIndex, string? selectedItems, int metadataCount)
    {
        if (!string.IsNullOrWhiteSpace(selectedItems))
        {
            var indices = selectedItems.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => int.TryParse(s, out _))
                .Select(int.Parse)
                .ToList();

            var pos = indices.IndexOf(playlistIndex);
            return pos >= 0 && pos < metadataCount ? pos : -1;
        }

        var idx = playlistIndex - 1;
        return idx >= 0 && idx < metadataCount ? idx : -1;
    }

    private static async Task<(int exitCode, string collectedStderr, string collectedStdout)> ExecuteYtdlp(
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
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.Latin1,
            StandardErrorEncoding = System.Text.Encoding.Latin1
        };
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

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

        stdout = ReencodeIfUtf8(stdout);

        return (process.ExitCode, string.Join("\n", stderrLines), stdout);
    }

    /// <summary>
    /// Stdout is read as Latin-1 (lossless single-byte). If yt-dlp actually output UTF-8
    /// (e.g. PYTHONUTF8=1 took effect), re-decode the raw bytes as UTF-8.
    /// </summary>
    private static string ReencodeIfUtf8(string latin1)
    {
        if (string.IsNullOrEmpty(latin1)) return latin1;
        var bytes = System.Text.Encoding.Latin1.GetBytes(latin1);
        try
        {
            var utf8Strict = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return utf8Strict.GetString(bytes);
        }
        catch
        {
            return latin1;
        }
    }

    private static bool IsAuthRequired(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return false;
        return AuthRequiredRegex.IsMatch(stderr);
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
