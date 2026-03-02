using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.FinTube.Models;

public class MusicBrainzReleaseTrack
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("recordingMbid")]
    public string RecordingMbid { get; set; } = "";

    [JsonPropertyName("trackNumber")]
    public string TrackNumber { get; set; } = "";

    [JsonPropertyName("position")]
    public int Position { get; set; }

    [JsonPropertyName("artistMbid")]
    public string ArtistMbid { get; set; } = "";

    [JsonPropertyName("artist")]
    public string Artist { get; set; } = "";
}
