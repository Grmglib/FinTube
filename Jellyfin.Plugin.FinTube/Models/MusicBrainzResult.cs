using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.FinTube.Models;

public class MusicBrainzResult
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("artist")]
    public string Artist { get; set; } = "";

    [JsonPropertyName("album")]
    public string Album { get; set; } = "";

    [JsonPropertyName("year")]
    public string Year { get; set; } = "";

    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("recordingMbid")]
    public string RecordingMbid { get; set; } = "";

    [JsonPropertyName("releaseMbid")]
    public string ReleaseMbid { get; set; } = "";

    [JsonPropertyName("artistMbid")]
    public string ArtistMbid { get; set; } = "";

    [JsonPropertyName("trackNumber")]
    public string TrackNumber { get; set; } = "";

    [JsonPropertyName("totalTracks")]
    public int TotalTracks { get; set; }

    [JsonPropertyName("genre")]
    public string Genre { get; set; } = "";
}
