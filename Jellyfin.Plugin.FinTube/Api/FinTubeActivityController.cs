using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.FinTube.Configuration;
using Jellyfin.Plugin.FinTube.Models;
using MediaBrowser.Controller.Library;
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
        private readonly ILibraryManager _libraryManager;

        public FinTubeActivityController(
            ILogger<FinTubeActivityController> logger,
            ILibraryManager libraryManager)
        {
            _logger = logger;
            _libraryManager = libraryManager;
        }

        public class FinTubeData
        {
            public string ytid { get; set; } = "";
            public string targetlibrary { get; set; } = "";
            public string targetfolder { get; set; } = "";
            public string targetfilename { get; set; } = "";
            public bool audioonly { get; set; } = false;
            public string preset { get; set; } = "balanced";
            public string customMaxRes { get; set; } = "1080";
            public string customVideoFormat { get; set; } = "mp4";
            public string customAudioFormat { get; set; } = "mp3";
            public string customAudioBitrate { get; set; } = "192";
            public bool isPlaylist { get; set; } = false;
            public string organizePath { get; set; } = "";
            public string metadataTitle { get; set; } = "";
            public string metadataArtist { get; set; } = "";
            public string metadataAlbum { get; set; } = "";
        }

        [HttpPost("submit_dl")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<Dictionary<string, object>> FinTubeDownload([FromBody] FinTubeData data)
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

                var args = BuildYtdlpArgs(data, targetPath);
                _logger.LogInformation("Exec: {exec} {args}", config.exec_YTDL, args);

                string? retryArgs = null;
                if (!string.IsNullOrWhiteSpace(config.cookiesBrowser))
                    retryArgs = BuildYtdlpArgs(data, targetPath, cookiesBrowser: config.cookiesBrowser);

                Action? onCompleted = null;
                if (config.enableAutoLibraryScan)
                {
                    var libraryManager = _libraryManager;
                    var logger = _logger;
                    onCompleted = () =>
                    {
                        logger.LogInformation("Triggering library scan after download...");
                        libraryManager.ValidateMediaLibrary(new Progress<double>(), System.Threading.CancellationToken.None);
                    };
                }

                var taskId = DownloadTaskManager.StartTask(
                    config.exec_YTDL, args, _logger,
                    isPlaylist: data.isPlaylist,
                    retryArgs: retryArgs,
                    onCompleted: onCompleted);

                return Ok(new Dictionary<string, object>
                {
                    { "taskId", taskId },
                    { "async", true },
                    { "message", data.isPlaylist ? "Playlist download started" : "Download started" }
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, e.Message);
                return StatusCode(500, new Dictionary<string, object>() { { "message", e.Message } });
            }
        }

        private static async Task<(int exitCode, string stdout, string stderr)> RunYtdlp(string executable, string args)
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

            var process = new Process() { StartInfo = startInfo };
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync();
            return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
        }

        private static bool IsAgeRestrictionError(string stderr)
        {
            if (string.IsNullOrWhiteSpace(stderr)) return false;
            return stderr.Contains("Sign in to confirm your age", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("age-restricted", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("This video may be inappropriate for some users", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildYtdlpArgs(FinTubeData data, string targetPath, string cookiesBrowser = "")
        {
            var preset = data.preset ?? "balanced";
            var args = new List<string>();

            if (!string.IsNullOrWhiteSpace(cookiesBrowser))
            {
                args.Add($"--cookies-from-browser {cookiesBrowser}");
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
                    case "custom":
                        var audioFmt = data.customAudioFormat ?? "mp3";
                        args.Add($"--audio-format {audioFmt}");
                        if (!string.Equals(audioFmt, "flac", StringComparison.OrdinalIgnoreCase))
                            args.Add($"--audio-quality {MapBitrateToQuality(data.customAudioBitrate)}");
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
                    case "custom":
                        var maxRes = data.customMaxRes ?? "1080";
                        var videoFmt = data.customVideoFormat ?? "mp4";
                        if (maxRes == "0")
                            args.Add("-f \"bestvideo+bestaudio/best\"");
                        else
                            args.Add($"-f \"bestvideo[height<={maxRes}]+bestaudio/best[height<={maxRes}]\"");
                        args.Add($"--merge-output-format {videoFmt}");
                        break;
                    default: // balanced
                        args.Add("-f \"bestvideo[height<=1080]+bestaudio/best[height<=1080]\"");
                        args.Add("--merge-output-format mp4");
                        break;
                }
            }

            args.Add("--embed-thumbnail");
            args.Add("--embed-metadata");

            if (!string.IsNullOrWhiteSpace(data.metadataTitle))
                args.Add($"--replace-in-metadata \"title\" \".+\" \"{data.metadataTitle.Replace("\"", "'")}\"");

            if (!string.IsNullOrWhiteSpace(data.metadataArtist))
                args.Add($"--parse-metadata \"{data.metadataArtist.Replace("\"", "'")}:%(artist)s\"");
            else
                args.Add("--parse-metadata \"%(uploader)s:%(artist)s\"");

            if (!string.IsNullOrWhiteSpace(data.metadataAlbum))
                args.Add($"--parse-metadata \"{data.metadataAlbum.Replace("\"", "'")}:%(album)s\"");

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
                var basePath = targetPath;
                if (data.audioonly && !string.IsNullOrWhiteSpace(data.organizePath))
                {
                    var parts = data.organizePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var sanitizedParts = parts.Select(SanitizePath).Where(p => !string.IsNullOrWhiteSpace(p));
                    basePath = System.IO.Path.Combine(new[] { targetPath }.Concat(sanitizedParts).ToArray());
                }

                if (!string.IsNullOrWhiteSpace(data.targetfilename))
                    outputTemplate = System.IO.Path.Combine(basePath, $"{data.targetfilename}.%(ext)s");
                else
                    outputTemplate = System.IO.Path.Combine(basePath, "%(title)s.%(ext)s");
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

                string args;
                if (isPlaylist)
                    args = $"-J --flat-playlist \"{query.Replace("\"", "\\\"")}\"";
                else if (isUrl)
                    args = $"-j --no-download --no-playlist \"{query.Replace("\"", "\\\"")}\"";
                else
                {
                    var searchArg = $"\"ytsearch{limit}:{query.Replace("\"", "\\\"")}\"";
                    args = $"-j --no-download --no-playlist {searchArg}";
                }

                _logger.LogInformation("FinTube Search: {exec} {args}", config.exec_YTDL, args);

                var (exitCode, output, errorOutput) = await RunYtdlp(config.exec_YTDL, args);

                if (exitCode != 0 && string.IsNullOrWhiteSpace(output) && isUrl && IsAgeRestrictionError(errorOutput))
                {
                    var browser = config.cookiesBrowser;
                    if (!string.IsNullOrWhiteSpace(browser))
                    {
                        _logger.LogWarning("Age-restriction detected in search, retrying with cookies from {browser}...", browser);
                        var retryArgs = $"--cookies-from-browser {browser} " + args;
                        (exitCode, output, errorOutput) = await RunYtdlp(config.exec_YTDL, retryArgs);
                    }
                    else
                    {
                        return StatusCode(500, new Dictionary<string, object>() {{"message", "This video is age-restricted. Go to FinTube Settings and select your browser in 'Cookies Browser' to enable authentication. Make sure you are logged into YouTube in that browser."}});
                    }
                }

                if (exitCode != 0 && string.IsNullOrWhiteSpace(output))
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
                { "failedVideos", task.FailedVideos },
                { "libraryScanQueued", task.LibraryScanQueued }
            };

            return Ok(response);
        }

        private static readonly HttpClient _mbClient = CreateMusicBrainzClient();

        private static HttpClient CreateMusicBrainzClient()
        {
            var client = new HttpClient();
            client.BaseAddress = new Uri("https://musicbrainz.org/ws/2/");
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FinTube", "1.1.0"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        private static int MapBitrateToQuality(string? bitrate)
        {
            return bitrate switch
            {
                "320" => 0,
                "256" => 2,
                "192" => 4,
                "128" => 6,
                "64" => 9,
                _ => 4
            };
        }

        private static string SanitizePath(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return sanitized.Trim().TrimEnd('.');
        }

        private static readonly string[] TitleSeparators = new[] { " - ", " – ", " — ", " | " };

        private static (string parsedArtist, string parsedTitle) ParseYouTubeTitle(string title, string channel)
        {
            foreach (var sep in TitleSeparators)
            {
                var idx = title.IndexOf(sep, StringComparison.Ordinal);
                if (idx > 0)
                {
                    var left = title[..idx].Trim();
                    var right = title[(idx + sep.Length)..].Trim();
                    // "Artist - Track" is the most common format
                    return (left, CleanTrackTitle(right));
                }
            }

            // No separator found: use full title as track, channel as artist
            return (channel, CleanTrackTitle(title));
        }

        private static string CleanTrackTitle(string title)
        {
            // Remove common YouTube suffixes like "(Official Video)", "[HD]", "(Lyrics)", etc.
            var cleaned = System.Text.RegularExpressions.Regex.Replace(
                title,
                @"\s*[\(\[](official\s*(music\s*)?video|official\s*audio|lyric\s*video|lyrics|hd|hq|4k|music\s*video|mv|visualizer|audio|video\s*oficial|clipe\s*oficial|remaster(ed)?|live)[\)\]]",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return cleaned.Trim();
        }

        private async Task<List<Models.MusicBrainzResult>> QueryMusicBrainz(string trackTitle, string artistName)
        {
            var queryParts = new List<string> { $"recording:\"{trackTitle}\"" };
            if (!string.IsNullOrWhiteSpace(artistName))
                queryParts.Add($"artist:\"{artistName}\"");

            var query = string.Join(" AND ", queryParts);
            var url = $"recording?query={Uri.EscapeDataString(query)}&fmt=json&limit=5";

            _logger.LogInformation("MusicBrainz query: {url}", url);

            var response = await _mbClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MusicBrainz API returned {status}", response.StatusCode);
                return new List<Models.MusicBrainzResult>();
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var results = new List<Models.MusicBrainzResult>();

            if (root.TryGetProperty("recordings", out var recordings) && recordings.ValueKind == JsonValueKind.Array)
            {
                foreach (var rec in recordings.EnumerateArray())
                {
                    var result = new Models.MusicBrainzResult();

                    if (rec.TryGetProperty("id", out var idEl))
                        result.RecordingMbid = idEl.GetString() ?? "";

                    if (rec.TryGetProperty("title", out var titleEl))
                        result.Title = titleEl.GetString() ?? "";

                    if (rec.TryGetProperty("score", out var scoreEl) && scoreEl.ValueKind == JsonValueKind.Number)
                        result.Score = scoreEl.GetInt32();

                    if (rec.TryGetProperty("artist-credit", out var credits) && credits.ValueKind == JsonValueKind.Array)
                    {
                        var artistNames = new List<string>();
                        foreach (var credit in credits.EnumerateArray())
                        {
                            if (credit.TryGetProperty("name", out var nameEl))
                                artistNames.Add(nameEl.GetString() ?? "");
                        }
                        result.Artist = string.Join(", ", artistNames);
                    }

                    if (rec.TryGetProperty("releases", out var releases) && releases.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var release in releases.EnumerateArray())
                        {
                            if (release.TryGetProperty("title", out var albumEl))
                                result.Album = albumEl.GetString() ?? "";
                            if (release.TryGetProperty("id", out var relIdEl))
                                result.ReleaseMbid = relIdEl.GetString() ?? "";
                            if (release.TryGetProperty("date", out var dateEl))
                            {
                                var dateStr = dateEl.GetString() ?? "";
                                result.Year = dateStr.Length >= 4 ? dateStr[..4] : dateStr;
                            }
                            break;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(result.Artist))
                        results.Add(result);
                }
            }

            return results;
        }

        [HttpGet("musicbrainz_search")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<Dictionary<string, object>>> MusicBrainzSearch([FromQuery] string title, [FromQuery] string artist = "")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(title))
                    return StatusCode(400, new Dictionary<string, object>() { { "message", "Title is required" } });

                var (parsedArtist, parsedTitle) = ParseYouTubeTitle(title, artist);
                _logger.LogInformation("MusicBrainz: parsed '{title}' + '{artist}' => track='{parsedTitle}' artist='{parsedArtist}'",
                    title, artist, parsedTitle, parsedArtist);

                var results = await QueryMusicBrainz(parsedTitle, parsedArtist);

                // Fallback: if no results with parsed artist, try with channel name if different
                if (results.Count == 0 && !string.IsNullOrWhiteSpace(artist)
                    && !string.Equals(parsedArtist, artist, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("MusicBrainz: no results, retrying with channel '{artist}' as artist", artist);
                    results = await QueryMusicBrainz(parsedTitle, artist);
                }

                // Fallback: try with just the track title, no artist filter
                if (results.Count == 0)
                {
                    _logger.LogInformation("MusicBrainz: no results, retrying without artist filter");
                    results = await QueryMusicBrainz(parsedTitle, "");
                }

                return Ok(new Dictionary<string, object>
                {
                    { "results", results },
                    { "count", results.Count },
                    { "parsedTitle", parsedTitle },
                    { "parsedArtist", parsedArtist }
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "MusicBrainz search failed");
                return StatusCode(500, new Dictionary<string, object>() { { "message", e.Message } });
            }
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
}