using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FinTube.Models;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.FinTube.Channel;

public class YouTubeChannel : IChannel
{
    public string Name => "FinTube YouTube";

    public string Description => "Search and download YouTube videos";

    public string DataVersion => "1";

    public string HomePageUrl => "https://youtube.com";

    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video, ChannelMediaType.Audio },
            ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Clip, ChannelMediaContentType.Song },
            SupportsContentDownloading = true,
            MaxPageSize = 30
        };
    }

    public bool IsEnabledFor(string userId) => true;

    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var cached = SearchResultsCache.LatestResults;

        var items = cached.Select(r => new ChannelItemInfo
        {
            Name = r.Title,
            Id = r.Id,
            ImageUrl = r.Thumbnail,
            Overview = FormatOverview(r),
            RunTimeTicks = r.Duration > 0 ? TimeSpan.FromSeconds(r.Duration).Ticks : (long?)null,
            Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Video,
            ContentType = ChannelMediaContentType.Clip,
            HomePageUrl = r.Url
        }).ToList();

        var result = new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count
        };

        return Task.FromResult(result);
    }

    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        return Task.FromResult(new DynamicImageResponse
        {
            HasImage = false
        });
    }

    public IEnumerable<ImageType> GetSupportedChannelImages()
    {
        return new List<ImageType> { ImageType.Primary };
    }

    private static string FormatOverview(YouTubeSearchResult r)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(r.Channel))
            parts.Add($"Channel: {r.Channel}");
        if (r.ViewCount > 0)
            parts.Add($"Views: {r.ViewCount:N0}");
        if (r.Duration > 0)
        {
            var ts = TimeSpan.FromSeconds(r.Duration);
            parts.Add($"Duration: {(ts.Hours > 0 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss"))}");
        }
        return string.Join(" | ", parts);
    }
}
