using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FinTube.Configuration;
using Jellyfin.Plugin.FinTube.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTube.ScheduledTasks;

/// <summary>
/// Scheduled task that scans the configured audio path and moves files to match FinTube organization rules (Artist/Album, track number prefix), producing a report.
/// </summary>
public class ReorganizeMusicTask : IScheduledTask
{
    private static readonly string[] AudioExtensions = { ".mp3", ".m4a", ".flac", ".ogg", ".opus", ".wma", ".aac" };

    private readonly ILogger<ReorganizeMusicTask> _logger;
    private readonly IApplicationPaths _applicationPaths;

    public ReorganizeMusicTask(ILogger<ReorganizeMusicTask> logger, IApplicationPaths applicationPaths)
    {
        _logger = logger;
        _applicationPaths = applicationPaths;
    }

    public string Name => "Reorganize music library (FinTube)";
    public string Key => "FinTubeReorganizeMusic";
    public string Description => "Scans the configured audio path, compares each file location to FinTube rules (Artist/Album, track number prefix) and moves misplaced files, producing a report.";
    public string Category => "Library";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var reportLines = new List<string>();
        var start = DateTime.UtcNow;
        reportLines.Add($"FinTube Reorganize Music Report — {start:yyyy-MM-dd HH:mm:ss} UTC");
        reportLines.Add("");

        var config = Plugin.Instance?.Configuration;
        var basePath = config?.defaultAudioPath?.Trim();
        var organizerEnabled = config?.enableMusicOrganizer ?? true;

        if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(basePath))
        {
            _logger.LogWarning("ReorganizeMusic: defaultAudioPath is empty or does not exist, skipping");
            reportLines.Add("Skipped: defaultAudioPath is not configured or directory does not exist.");
            WriteReport(reportLines, 0, 0, 0);
            progress.Report(100);
            return Task.CompletedTask;
        }

        if (!organizerEnabled)
        {
            _logger.LogInformation("ReorganizeMusic: enableMusicOrganizer is false, skipping");
            reportLines.Add("Skipped: Music organizer is disabled in plugin settings.");
            WriteReport(reportLines, 0, 0, 0);
            progress.Report(100);
            return Task.CompletedTask;
        }

        var files = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(basePath, "*.*", SearchOption.AllDirectories))
            {
                if (cancellationToken.IsCancellationRequested) break;
                var ext = Path.GetExtension(path);
                if (AudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    files.Add(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReorganizeMusic: failed to enumerate audio path");
            reportLines.Add($"ERROR: Failed to scan directory: {ex.Message}");
            WriteReport(reportLines, 0, 0, 0);
            progress.Report(100);
            return Task.CompletedTask;
        }

        var total = files.Count;
        var moved = 0;
        var skipped = 0;
        var index = 0;

        foreach (var currentPath in files)
        {
            if (cancellationToken.IsCancellationRequested) break;
            index++;
            progress.Report(total > 0 ? (index * 100.0 / total) : 100);

            var metadata = MusicPostProcessor.ReadMetadataFromFile(currentPath);
            if (metadata == null || string.IsNullOrWhiteSpace(metadata.Artist))
            {
                skipped++;
                reportLines.Add($"SKIPPED (no/invalid metadata): {currentPath}");
                continue;
            }

            var ext = Path.GetExtension(currentPath);
            if (string.IsNullOrWhiteSpace(metadata.Title))
                metadata = new MusicMetadata { Title = Path.GetFileNameWithoutExtension(currentPath) ?? "", Artist = metadata.Artist, Album = metadata.Album, Year = metadata.Year, TrackNumber = metadata.TrackNumber, TotalTracks = metadata.TotalTracks, Genre = metadata.Genre, ArtistMbid = metadata.ArtistMbid, ReleaseMbid = metadata.ReleaseMbid, RecordingMbid = metadata.RecordingMbid };

            var (expectedDir, expectedFileName) = MusicPostProcessor.GetExpectedOrganizedPath(basePath, metadata, ext);
            var expectedPath = Path.Combine(expectedDir, expectedFileName);

            var currentNormalized = Path.GetFullPath(currentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var expectedNormalized = Path.GetFullPath(expectedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(currentNormalized, expectedNormalized, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                Directory.CreateDirectory(expectedDir);
                File.Move(currentPath, expectedPath, overwrite: true);
                moved++;
                reportLines.Add($"MOVED: {currentPath} -> {expectedPath}");
                _logger.LogInformation("ReorganizeMusic: moved '{src}' -> '{dest}'", currentPath, expectedPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ReorganizeMusic: failed to move '{path}'", currentPath);
                reportLines.Add($"ERROR: {currentPath} - {ex.Message}");
            }
        }

        reportLines.Add("");
        reportLines.Add($"Summary: {total} files scanned, {moved} moved, {skipped} skipped.");
        _logger.LogInformation("ReorganizeMusic: {Total} files scanned, {Moved} moved, {Skipped} skipped", total, moved, skipped);
        WriteReport(reportLines, total, moved, skipped);
        progress.Report(100);
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }

    private void WriteReport(List<string> lines, int total, int moved, int skipped)
    {
        try
        {
            var dir = Path.Combine(_applicationPaths.DataPath, "FinTube");
            Directory.CreateDirectory(dir);
            var fileName = $"ReorganizeMusicReport_{DateTime.UtcNow:yyyy-MM-dd_HHmmss}.txt";
            var path = Path.Combine(dir, fileName);
            File.WriteAllLines(path, lines);
            _logger.LogInformation("ReorganizeMusic: report written to {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReorganizeMusic: failed to write report file");
        }
    }
}
