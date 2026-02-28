using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.FinTube.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        exec_YTDL = "/usr/local/bin/yt-dlp";
        exec_ID3 = "/usr/bin/id3v2";
        defaultVideoPath = "";
        defaultAudioPath = "";
        downloadPreset = "balanced";
        cookiesFilePath = "";
    }

    /// <summary>
    /// Executable for youtube-dl/youtube-dlp
    /// </summary>
    public string exec_YTDL { get; set; }

    /// <summary>
    /// Executable for ID3v2
    /// </summary>
    public string exec_ID3 { get; set; }

    /// <summary>
    /// Default download path for video files
    /// </summary>
    public string defaultVideoPath { get; set; }

    /// <summary>
    /// Default download path for audio files
    /// </summary>
    public string defaultAudioPath { get; set; }

    /// <summary>
    /// Download quality preset: "best", "balanced", or "small"
    /// </summary>
    public string downloadPreset { get; set; }

    /// <summary>
    /// Path to a Netscape-format cookies.txt file for YouTube authentication (age-restricted content)
    /// </summary>
    public string cookiesFilePath { get; set; }
}
