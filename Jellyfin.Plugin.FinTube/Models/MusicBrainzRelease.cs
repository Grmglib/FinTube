using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.FinTube.Models;

public class MusicBrainzRelease
{
    [JsonPropertyName("releaseMbid")]
    public string ReleaseMbid { get; set; } = "";

    [JsonPropertyName("albumName")]
    public string AlbumName { get; set; } = "";

    [JsonPropertyName("year")]
    public string Year { get; set; } = "";

    [JsonPropertyName("trackNumber")]
    public string TrackNumber { get; set; } = "";

    [JsonPropertyName("totalTracks")]
    public int TotalTracks { get; set; }

    [JsonPropertyName("country")]
    public string Country { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
}
