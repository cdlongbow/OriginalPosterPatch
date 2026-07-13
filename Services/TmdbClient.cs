// Services/TmdbClient.cs
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Serialization;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using OriginalPoster.Models;

namespace OriginalPoster.Services;

/// <summary>
/// TMDB API 客户端，封装网络请求和反序列化
/// 严格使用 Emby 提供的 IHttpClient 和 IJsonSerializer
/// </summary>
public class TmdbClient
{
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private readonly IHttpClient _httpClient;
    private readonly IJsonSerializer _jsonSerializer;
    private readonly string _apiKey;

    public TmdbClient(IHttpClient httpClient, IJsonSerializer jsonSerializer, string apiKey)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
    }

    /// <summary>
    /// 获取项目详情（用于获取 production_countries）
    /// </summary>
    public async Task<TmdbItemDetails> GetItemDetailsAsync(
        string tmdbId,
        string type, // "movie", "tv", "collection"
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tmdbId))
            throw new ArgumentException("TMDB ID cannot be null or empty.", nameof(tmdbId));
        
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("Type cannot be null or empty.", nameof(type));

        var encodedApiKey = Uri.EscapeDataString(_apiKey);
        var encodedTmdbId = Uri.EscapeDataString(tmdbId.Trim());
        var url = $"{BaseUrl}/{type}/{encodedTmdbId}?api_key={encodedApiKey}";

        var options = new HttpRequestOptions
        {
            Url = url,
            CancellationToken = cancellationToken,
            TimeoutMs = 10000
        };

        using (var response = await _httpClient.GetResponse(options).ConfigureAwait(false))
        using (var stream = response.Content)
        {
            return await _jsonSerializer.DeserializeFromStreamAsync<TmdbItemDetails>(stream).ConfigureAwait(false)
                   ?? new TmdbItemDetails();
        }
    }

    /// <summary>
    /// 获取指定项目（电影/剧集）的图像列表
    /// </summary>
    public async Task<TmdbImageResult> GetImagesAsync(
        string tmdbId,
        string type, // "movie", "tv", "collection", "tv/{seriesId}/season/{seasonNumber}"
        string language,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tmdbId))
            throw new ArgumentException("TMDB ID cannot be null or empty.", nameof(tmdbId));

        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("Type cannot be null or empty.", nameof(type));

        var encodedApiKey = Uri.EscapeDataString(_apiKey);
        var normalizedLanguage = NormalizeImageLanguage(language);
        var encodedLanguage = Uri.EscapeDataString(normalizedLanguage);
        string url;

        // 处理播出季格式 "SeriesId_S<SeasonNumber>" -> "tv/{seriesId}/season/{seasonNumber}"
        if (type == "tv_season")
        {
            var parts = tmdbId.Split(new[] { "_S" }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seriesId)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seasonNumber))
            {
                var seasonPath = $"tv/{seriesId}/season/{seasonNumber}";
                url = $"{BaseUrl}/{seasonPath}/images?" +
                      $"api_key={encodedApiKey}&" +
                      $"language={encodedLanguage},null";
            }
            else
            {
                throw new ArgumentException($"Invalid composite season TMDB ID format: {tmdbId}", nameof(tmdbId));
            }
        }
        else
        {
            var encodedTmdbId = Uri.EscapeDataString(tmdbId.Trim());
            url = $"{BaseUrl}/{type}/{encodedTmdbId}/images?" +
                  $"api_key={encodedApiKey}&" +
                  $"language={encodedLanguage},null";
        }

        var options = new HttpRequestOptions
        {
            Url = url,
            CancellationToken = cancellationToken,
            TimeoutMs = 10000
        };

        using (var response = await _httpClient.GetResponse(options).ConfigureAwait(false))
        using (var stream = response.Content)
        {
            return await _jsonSerializer.DeserializeFromStreamAsync<TmdbImageResult>(stream).ConfigureAwait(false)
                   ?? new TmdbImageResult();
        }
    }

    private static string NormalizeImageLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "en";

        var normalized = language.Trim();
        if (string.Equals(normalized, "cn", StringComparison.OrdinalIgnoreCase))
            return "zh";

        var separatorIndex = normalized.IndexOf('-');
        if (separatorIndex > 0)
            normalized = normalized.Substring(0, separatorIndex);

        return normalized.ToLowerInvariant();
    }
}
