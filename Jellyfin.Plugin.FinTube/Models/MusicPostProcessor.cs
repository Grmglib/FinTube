using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TagLib;

namespace Jellyfin.Plugin.FinTube.Models;

public class MusicMetadata
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Year { get; set; } = "";
    public string TrackNumber { get; set; } = "";
    public int TotalTracks { get; set; }
    public string Genre { get; set; } = "";
    public string ArtistMbid { get; set; } = "";
    public string ReleaseMbid { get; set; } = "";
    public string RecordingMbid { get; set; } = "";
}

public static class MusicPostProcessor
{
    private static readonly HttpClient _httpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FinTube", "1.1.0"));
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    public static async Task ProcessAsync(string filePath, MusicMetadata metadata, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
        {
            logger.LogWarning("PostProcessor: file not found at '{path}', skipping", filePath);
            return;
        }

        try
        {
            using var tagFile = TagLib.File.Create(filePath);

            WriteMetadata(tagFile, metadata, logger);
            await ReplaceCoverArt(tagFile, metadata.ReleaseMbid, logger);

            tagFile.Save();
            logger.LogInformation("PostProcessor: successfully processed '{path}'", filePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PostProcessor: failed to process '{path}'", filePath);
        }
    }

    private static void WriteMetadata(TagLib.File tagFile, MusicMetadata metadata, ILogger logger)
    {
        var tag = tagFile.Tag;

        if (!string.IsNullOrWhiteSpace(metadata.Title))
            tag.Title = metadata.Title;

        if (!string.IsNullOrWhiteSpace(metadata.Artist))
        {
            tag.Performers = new[] { metadata.Artist };
            tag.AlbumArtists = new[] { metadata.Artist };
        }

        if (!string.IsNullOrWhiteSpace(metadata.Album))
            tag.Album = metadata.Album;

        if (!string.IsNullOrWhiteSpace(metadata.Year) && uint.TryParse(metadata.Year, out var year))
            tag.Year = year;

        if (!string.IsNullOrWhiteSpace(metadata.TrackNumber) && uint.TryParse(metadata.TrackNumber, out var trackNum))
            tag.Track = trackNum;

        if (metadata.TotalTracks > 0)
            tag.TrackCount = (uint)metadata.TotalTracks;

        if (!string.IsNullOrWhiteSpace(metadata.Genre))
            tag.Genres = new[] { metadata.Genre };

        WriteMusicBrainzIds(tagFile, metadata, logger);
    }

    private static void WriteMusicBrainzIds(TagLib.File tagFile, MusicMetadata metadata, ILogger logger)
    {
        if (tagFile.Tag is TagLib.Id3v2.Tag id3v2)
        {
            SetId3v2UserTextFrame(id3v2, "MusicBrainz Artist Id", metadata.ArtistMbid);
            SetId3v2UserTextFrame(id3v2, "MusicBrainz Album Artist Id", metadata.ArtistMbid);
            SetId3v2UserTextFrame(id3v2, "MusicBrainz Album Id", metadata.ReleaseMbid);
            SetId3v2UserTextFrame(id3v2, "MusicBrainz Release Track Id", metadata.RecordingMbid);
        }
        else if (tagFile.Tag is TagLib.Ogg.XiphComment xiph)
        {
            SetXiphField(xiph, "MUSICBRAINZ_ARTISTID", metadata.ArtistMbid);
            SetXiphField(xiph, "MUSICBRAINZ_ALBUMARTISTID", metadata.ArtistMbid);
            SetXiphField(xiph, "MUSICBRAINZ_ALBUMID", metadata.ReleaseMbid);
            SetXiphField(xiph, "MUSICBRAINZ_RELEASETRACKID", metadata.RecordingMbid);
        }
        else
        {
            var combined = tagFile.Tag as TagLib.CombinedTag;
            if (combined != null)
            {
                var id3 = combined.Tags.OfType<TagLib.Id3v2.Tag>().FirstOrDefault();
                if (id3 != null)
                {
                    SetId3v2UserTextFrame(id3, "MusicBrainz Artist Id", metadata.ArtistMbid);
                    SetId3v2UserTextFrame(id3, "MusicBrainz Album Artist Id", metadata.ArtistMbid);
                    SetId3v2UserTextFrame(id3, "MusicBrainz Album Id", metadata.ReleaseMbid);
                    SetId3v2UserTextFrame(id3, "MusicBrainz Release Track Id", metadata.RecordingMbid);
                    return;
                }

                var xiphTag = combined.Tags.OfType<TagLib.Ogg.XiphComment>().FirstOrDefault();
                if (xiphTag != null)
                {
                    SetXiphField(xiphTag, "MUSICBRAINZ_ARTISTID", metadata.ArtistMbid);
                    SetXiphField(xiphTag, "MUSICBRAINZ_ALBUMARTISTID", metadata.ArtistMbid);
                    SetXiphField(xiphTag, "MUSICBRAINZ_ALBUMID", metadata.ReleaseMbid);
                    SetXiphField(xiphTag, "MUSICBRAINZ_RELEASETRACKID", metadata.RecordingMbid);
                }
            }
        }
    }

    private static void SetId3v2UserTextFrame(TagLib.Id3v2.Tag tag, string description, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        var existing = TagLib.Id3v2.UserTextInformationFrame.Get(tag, description, true);
        existing.Text = new[] { value };
    }

    private static void SetXiphField(TagLib.Ogg.XiphComment tag, string fieldName, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        tag.SetField(fieldName, value);
    }

    private static async Task ReplaceCoverArt(TagLib.File tagFile, string releaseMbid, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(releaseMbid))
        {
            logger.LogInformation("PostProcessor: no releaseMbid, keeping existing artwork");
            return;
        }

        var coverUrl = $"https://coverartarchive.org/release/{releaseMbid}/front";

        try
        {
            logger.LogInformation("PostProcessor: downloading cover art from {url}", coverUrl);
            var response = await _httpClient.GetAsync(coverUrl);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("PostProcessor: Cover Art Archive returned {status}, keeping existing artwork", response.StatusCode);
                return;
            }

            var imageData = await response.Content.ReadAsByteArrayAsync();
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";

            var mimeType = contentType.Contains("png") ? "image/png" : "image/jpeg";

            var picture = new Picture(new ByteVector(imageData))
            {
                Type = PictureType.FrontCover,
                MimeType = mimeType,
                Description = "Cover"
            };

            tagFile.Tag.Pictures = new IPicture[] { picture };
            logger.LogInformation("PostProcessor: cover art replaced successfully ({bytes} bytes)", imageData.Length);
        }
        catch (TaskCanceledException)
        {
            logger.LogWarning("PostProcessor: cover art download timed out, keeping existing artwork");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "PostProcessor: failed to download cover art, keeping existing artwork");
        }
    }
}
