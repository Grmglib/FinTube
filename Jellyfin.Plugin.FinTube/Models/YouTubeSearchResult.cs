using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.FinTube.Models;

public class YouTubeSearchResult
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "";

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonPropertyName("thumbnail")]
    public string Thumbnail { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("viewCount")]
    public long ViewCount { get; set; }
}
