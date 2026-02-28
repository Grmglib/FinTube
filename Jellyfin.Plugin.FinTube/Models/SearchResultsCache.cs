using System.Collections.Generic;

namespace Jellyfin.Plugin.FinTube.Models;

public static class SearchResultsCache
{
    private static readonly object _lock = new();
    private static List<YouTubeSearchResult> _latestResults = new();
    private static string _latestQuery = "";

    public static string LatestQuery
    {
        get { lock (_lock) return _latestQuery; }
        set { lock (_lock) _latestQuery = value; }
    }

    public static List<YouTubeSearchResult> LatestResults
    {
        get { lock (_lock) return new List<YouTubeSearchResult>(_latestResults); }
        set { lock (_lock) _latestResults = new List<YouTubeSearchResult>(value); }
    }
}
