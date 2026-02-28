using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.FinTube.Models;

public enum DownloadTaskStatus
{
    Queued,
    Running,
    Completed,
    CompletedWithErrors,
    Failed
}

public class DownloadTaskInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DownloadTaskStatus Status { get; set; } = DownloadTaskStatus.Queued;

    [JsonPropertyName("progress")]
    public string Progress { get; set; } = "";

    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; set; }

    [JsonPropertyName("errorMessage")]
    public string ErrorMessage { get; set; } = "";

    [JsonPropertyName("isPlaylist")]
    public bool IsPlaylist { get; set; }

    [JsonPropertyName("videoCount")]
    public int VideoCount { get; set; }

    [JsonPropertyName("completedCount")]
    public int CompletedCount { get; set; }

    [JsonPropertyName("failedCount")]
    public int FailedCount { get; set; }

    [JsonPropertyName("failedVideos")]
    public List<string> FailedVideos { get; set; } = new();
}
