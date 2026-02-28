using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data.Entities;
using Jellyfin.Plugin.FinTube.Configuration;
using Jellyfin.Plugin.FinTube.Models;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTube.Api;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("fintube")]
[Produces(MediaTypeNames.Application.Json)]
public class FinTubeActivityController : ControllerBase
{
        private readonly ILogger<FinTubeActivityController> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IFileSystem _fileSystem;
        private readonly IServerConfigurationManager _config;
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;

        public FinTubeActivityController(
            ILoggerFactory loggerFactory,
            IFileSystem fileSystem,
            IServerConfigurationManager config,
            IUserManager userManager,
            ILibraryManager libraryManager)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<FinTubeActivityController>();
            _fileSystem = fileSystem;
            _config = config;
            _userManager = userManager;
            _libraryManager = libraryManager;

            _logger.LogInformation("FinTubeActivityController Loaded");
        }

        public class FinTubeData
        {
            public string ytid { get; set; } = "";
            public string targetlibrary { get; set; } = "";
            public string targetfolder { get; set; } = "";
            public string targetfilename { get; set; } = "";
            public bool audioonly { get; set; } = false;
            public string preset { get; set; } = "balanced";
            public bool isPlaylist { get; set; } = false;
        }

        [HttpPost("submit_dl")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<Dictionary<string, object>>> FinTubeDownload([FromBody] FinTubeData data)
        {
            try
            {
                _logger.LogInformation("FinTubeDownload: {ytid} audioonly={audioonly} preset={preset} isPlaylist={isPlaylist}", data.ytid, data.audioonly, data.preset, data.isPlaylist);

                PluginConfiguration? config = Plugin.Instance?.Configuration;
                if (config == null || !System.IO.File.Exists(config.exec_YTDL))
                    throw new Exception("YT-DL Executable configured incorrectly");

                data.targetfolder = String.Join("/", data.targetfolder.Split("/", StringSplitOptions.RemoveEmptyEntries));
                String targetPath = data.targetlibrary.TrimEnd('/') + (string.IsNullOrEmpty(data.targetfolder) ? "" : "/" + data.targetfolder);

                if (!System.IO.Directory.CreateDirectory(targetPath).Exists)
                    throw new Exception("Directory could not be created");

                var args = BuildYtdlpArgs(data, targetPath, config.cookiesFilePath);

                _logger.LogInformation("Exec: {exec} {args}", config.exec_YTDL, args);

                if (data.isPlaylist)
                {
                    var taskId = DownloadTaskManager.StartTask(config.exec_YTDL, args, _logger, isPlaylist: true);
                    return Ok(new Dictionary<string, object>
                    {
                        { "taskId", taskId },
                        { "async", true },
                        { "message", "Playlist download started" }
                    });
                }

                var startInfo = new ProcessStartInfo()
                {
                    FileName = config.exec_YTDL,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var process = new Process() { StartInfo = startInfo };
                process.Start();
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                await Task.WhenAll(stdoutTask, stderrTask);
                await process.WaitForExitAsync();
                var stdout = stdoutTask.Result;
                var stderr = stderrTask.Result;

                var status = $"Preset: {data.preset}<br>";
                status += $"Command: yt-dlp {args}<br>";

                if (process.ExitCode != 0)
                {
                    _logger.LogError("yt-dlp failed (exit {code}): {stderr}", process.ExitCode, stderr);
                    throw new Exception($"yt-dlp exited with code {process.ExitCode}: {stderr}");
                }

                status += "<font color='green'>File Saved!</font>";

                var response = new Dictionary<string, object> { { "message", status } };
                return Ok(response);
            }
            catch (Exception e)
            {
                _logger.LogError(e, e.Message);
                return StatusCode(500, new Dictionary<string, object>() { { "message", e.Message } });
            }
        }

        private static string BuildYtdlpArgs(FinTubeData data, string targetPath, string cookiesFilePath = "")
        {
            var preset = data.preset ?? "balanced";
            var args = new List<string>();

            if (!string.IsNullOrWhiteSpace(cookiesFilePath) && System.IO.File.Exists(cookiesFilePath))
            {
                args.Add($"--cookies \"{cookiesFilePath}\"");
            }

            if (data.audioonly)
            {
                args.Add("-x");
                switch (preset)
                {
                    case "best":
                        args.Add("--audio-format mp3");
                        args.Add("--audio-quality 0");
                        break;
                    case "small":
                        args.Add("--audio-format mp3");
                        args.Add("--audio-quality 9");
                        break;
                    default: // balanced
                        args.Add("--audio-format mp3");
                        args.Add("--audio-quality 5");
                        break;
                }
            }
            else
            {
                switch (preset)
                {
                    case "best":
                        args.Add("-f \"bestvideo+bestaudio/best\"");
                        args.Add("--merge-output-format mkv");
                        break;
                    case "small":
                        args.Add("-f \"bestvideo[height<=720]+bestaudio/best[height<=720]\"");
                        args.Add("--merge-output-format mp4");
                        break;
                    default: // balanced
                        args.Add("-f \"bestvideo[height<=1080]+bestaudio/best[height<=1080]\"");
                        args.Add("--merge-output-format mp4");
                        break;
                }
            }

            // Embed metadata and thumbnail in all presets
            args.Add("--embed-thumbnail");
            args.Add("--embed-metadata");
            args.Add("--parse-metadata \"%(uploader)s:%(artist)s\"");
            args.Add("--parse-metadata \"%(upload_date>%Y)s:%(meta_date)s\"");

            string outputTemplate;
            if (data.isPlaylist)
            {
                args.Add("--yes-playlist");
                args.Add("--ignore-errors");
                outputTemplate = System.IO.Path.Combine(targetPath, "%(playlist_title)s", "%(title)s.%(ext)s");
            }
            else
            {
                args.Add("--no-playlist");
                if (!string.IsNullOrWhiteSpace(data.targetfilename))
                    outputTemplate = System.IO.Path.Combine(targetPath, $"{data.targetfilename}.%(ext)s");
                else
                    outputTemplate = System.IO.Path.Combine(targetPath, "%(title)s.%(ext)s");
            }

            args.Add($"-o \"{outputTemplate}\"");
            args.Add(data.ytid);

            return string.Join(" ", args);
        }

        [HttpGet("libraries")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<Dictionary<string, object>> FinTubeLibraries()
        {
            try
            {
                _logger.LogInformation("FinTubeDLibraries count: {count}", _libraryManager.GetVirtualFolders().Count);

                Dictionary<string, object> response = new Dictionary<string, object>();
                response.Add("data", _libraryManager.GetVirtualFolders().Select(i => i.Locations).ToArray());
                return Ok(response);
            }
            catch(Exception e)
            {
                _logger.LogError(e, e.Message);
                return StatusCode(500, new Dictionary<string, object>() {{"message", e.Message}});
            }
        }

        [HttpGet("browse")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<Dictionary<string, object>> BrowseDirectory([FromQuery] string path = "/")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    path = "/";

                var dirInfo = new DirectoryInfo(path);
                if (!dirInfo.Exists)
                    return StatusCode(404, new Dictionary<string, object>() {{"message", $"Directory not found: {path}"}});

                var directories = dirInfo.GetDirectories()
                    .Where(d => (d.Attributes & FileAttributes.Hidden) == 0)
                    .Select(d => new { name = d.Name, path = d.FullName })
                    .OrderBy(d => d.name)
                    .ToArray();

                var files = dirInfo.GetFiles()
                    .Where(f => (f.Attributes & FileAttributes.Hidden) == 0)
                    .Select(f => new { name = f.Name, path = f.FullName })
                    .OrderBy(f => f.name)
                    .ToArray();

                var response = new Dictionary<string, object>
                {
                    { "current", dirInfo.FullName },
                    { "parent", dirInfo.Parent?.FullName ?? "" },
                    { "directories", directories },
                    { "files", files }
                };

                return Ok(response);
            }
            catch (UnauthorizedAccessException)
            {
                return StatusCode(403, new Dictionary<string, object>() {{"message", $"Access denied: {path}"}});
            }
            catch (Exception e)
            {
                _logger.LogError(e, e.Message);
                return StatusCode(500, new Dictionary<string, object>() {{"message", e.Message}});
            }
        }

        private static bool IsYouTubeUrl(string query)
        {
            return query.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || query.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPlaylistUrl(string query)
        {
            return IsYouTubeUrl(query)
                && (query.Contains("list=", StringComparison.OrdinalIgnoreCase)
                    || query.Contains("/playlist", StringComparison.OrdinalIgnoreCase));
        }

        private YouTubeSearchResult ParseVideoJson(JsonElement root)
        {
            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(id)) return null!;

            var thumbnail = "";
            if (root.TryGetProperty("thumbnail", out var thumbEl))
                thumbnail = thumbEl.GetString() ?? "";
            if (string.IsNullOrEmpty(thumbnail))
                thumbnail = $"https://img.youtube.com/vi/{id}/mqdefault.jpg";

            return new YouTubeSearchResult
            {
                Id = id,
                Title = root.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "",
                Channel = root.TryGetProperty("channel", out var chEl) ? chEl.GetString() ?? ""
                        : root.TryGetProperty("uploader", out var upEl) ? upEl.GetString() ?? "" : "",
                Duration = root.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number ? durEl.GetDouble() : 0,
                Thumbnail = thumbnail,
                Url = $"https://www.youtube.com/watch?v={id}",
                ViewCount = root.TryGetProperty("view_count", out var vcEl) && vcEl.ValueKind == JsonValueKind.Number ? vcEl.GetInt64() : 0
            };
        }

        [HttpGet("search")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<Dictionary<string, object>>> SearchYouTube([FromQuery] string query, [FromQuery] int limit = 10)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(query))
                    return StatusCode(400, new Dictionary<string, object>() {{"message", "Query is required"}});

                PluginConfiguration? config = Plugin.Instance?.Configuration;
                if (config == null || !System.IO.File.Exists(config.exec_YTDL))
                    return StatusCode(500, new Dictionary<string, object>() {{"message", "YT-DL executable not configured correctly"}});

                if (limit < 1) limit = 1;
                if (limit > 30) limit = 30;

                bool isPlaylist = IsPlaylistUrl(query);
                bool isUrl = IsYouTubeUrl(query);

                var cookiesArg = "";
                if (!string.IsNullOrWhiteSpace(config.cookiesFilePath) && System.IO.File.Exists(config.cookiesFilePath))
                {
                    cookiesArg = $"--cookies \"{config.cookiesFilePath}\" ";
                }

                string args;
                if (isPlaylist)
                    args = $"{cookiesArg}-J --flat-playlist \"{query.Replace("\"", "\\\"")}\"";
                else if (isUrl)
                    args = $"{cookiesArg}-j --no-download --no-playlist \"{query.Replace("\"", "\\\"")}\"";
                else
                {
                    var searchArg = $"\"ytsearch{limit}:{query.Replace("\"", "\\\"")}\"";
                    args = $"{cookiesArg}-j --no-download --no-playlist {searchArg}";
                }

                _logger.LogInformation("FinTube Search: {exec} {args}", config.exec_YTDL, args);

                var startInfo = new ProcessStartInfo()
                {
                    FileName = config.exec_YTDL,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var process = new Process() { StartInfo = startInfo };
                process.Start();

                var output = await process.StandardOutput.ReadToEndAsync();
                var errorOutput = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
                {
                    _logger.LogError("yt-dlp search failed: {error}", errorOutput);
                    return StatusCode(500, new Dictionary<string, object>() {{"message", $"Search failed: {errorOutput}"}});
                }

                var results = new List<YouTubeSearchResult>();
                string playlistTitle = "";
                string playlistUrl = "";

                if (isPlaylist)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(output);
                        var root = doc.RootElement;

                        playlistTitle = root.TryGetProperty("title", out var ptEl) ? ptEl.GetString() ?? "" : "";
                        playlistUrl = root.TryGetProperty("webpage_url", out var puEl) ? puEl.GetString() ?? query : query;

                        if (root.TryGetProperty("entries", out var entriesEl) && entriesEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var entry in entriesEl.EnumerateArray())
                            {
                                var result = ParseVideoJson(entry);
                                if (result != null)
                                    results.Add(result);
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning("Failed to parse playlist JSON: {error}", ex.Message);
                    }
                }
                else
                {
                    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var result = ParseVideoJson(doc.RootElement);
                            if (result != null)
                                results.Add(result);
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogWarning("Failed to parse search result line: {error}", ex.Message);
                        }
                    }
                }

                SearchResultsCache.LatestQuery = query;
                SearchResultsCache.LatestResults = results;

                var response = new Dictionary<string, object>
                {
                    { "query", query },
                    { "results", results },
                    { "count", results.Count },
                    { "isPlaylist", isPlaylist },
                    { "playlistTitle", playlistTitle },
                    { "playlistUrl", playlistUrl }
                };

                return Ok(response);
            }
            catch (Exception e)
            {
                _logger.LogError(e, e.Message);
                return StatusCode(500, new Dictionary<string, object>() {{"message", e.Message}});
            }
        }

        [HttpGet("task_status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<Dictionary<string, object>> GetTaskStatus([FromQuery] string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return StatusCode(400, new Dictionary<string, object>() { { "message", "Task ID is required" } });

            var task = DownloadTaskManager.GetTask(id);
            if (task == null)
                return StatusCode(404, new Dictionary<string, object>() { { "message", $"Task not found: {id}" } });

            var response = new Dictionary<string, object>
            {
                { "id", task.Id },
                { "status", task.Status.ToString() },
                { "progress", task.Progress },
                { "completedCount", task.CompletedCount },
                { "videoCount", task.VideoCount },
                { "isPlaylist", task.IsPlaylist },
                { "error", task.ErrorMessage },
                { "failedCount", task.FailedCount },
                { "failedVideos", task.FailedVideos }
            };

            return Ok(response);
        }

        [HttpGet("validate_path")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<Dictionary<string, object>> ValidatePath([FromQuery] string path)
        {
            var response = new Dictionary<string, object>
            {
                { "exists", System.IO.File.Exists(path) },
                { "path", path ?? "" }
            };
            return Ok(response);
        }

        private static Process createProcess(String exe, String args)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo() { FileName = exe, Arguments = args };
            return new Process() { StartInfo = startInfo };
        }
}