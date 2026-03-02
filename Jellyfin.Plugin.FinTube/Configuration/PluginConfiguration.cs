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
        defaultVideoPath = "";
        defaultAudioPath = "";
        downloadPreset = "balanced";
        customMaxResolution = "1080";
        customVideoFormat = "mp4";
        customAudioFormat = "mp3";
        customAudioBitrate = "192";
        cookiesBrowser = "";
        cookiesFile = "";
        enableMusicOrganizer = true;
        enableAutoLibraryScan = true;
        enableCoverArtReplacement = true;
    }

    /// <summary>
    /// Executable for youtube-dl/youtube-dlp
    /// </summary>
    public string exec_YTDL { get; set; }

    /// <summary>
    /// Default download path for video files
    /// </summary>
    public string defaultVideoPath { get; set; }

    /// <summary>
    /// Default download path for audio files
    /// </summary>
    public string defaultAudioPath { get; set; }

    /// <summary>
    /// Download quality preset: "best", "balanced", "small", or "custom"
    /// </summary>
    public string downloadPreset { get; set; }

    public string customMaxResolution { get; set; }

    public string customVideoFormat { get; set; }

    public string customAudioFormat { get; set; }

    public string customAudioBitrate { get; set; }

    /// <summary>
    /// Browser to read YouTube cookies from for age-restricted content (e.g. "firefox", "chrome", "edge")
    /// </summary>
    public string cookiesBrowser { get; set; }

    /// <summary>
    /// Path to a Netscape-format cookies.txt file for YouTube authentication.
    /// Takes priority over cookiesBrowser when both are set.
    /// </summary>
    public string cookiesFile { get; set; }

    /// <summary>
    /// When enabled, audio downloads query MusicBrainz to identify artist/album and organize files into Artist/Album folders
    /// </summary>
    public bool enableMusicOrganizer { get; set; }

    /// <summary>
    /// When enabled, automatically trigger a Jellyfin library scan after each download completes
    /// </summary>
    public bool enableAutoLibraryScan { get; set; }

    /// <summary>
    /// When enabled, replaces the YouTube thumbnail with the album cover from Cover Art Archive
    /// and rewrites metadata tags using TagLib# after download completes
    /// </summary>
    public bool enableCoverArtReplacement { get; set; }
}
