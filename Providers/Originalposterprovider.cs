// Providers/Originalposterprovider.cs
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using OriginalPoster.Models;
using OriginalPoster.Services;

namespace OriginalPoster.Providers;

public class OriginalPosterProvider : IRemoteImageProvider, IHasOrder
{
    private readonly IHttpClient _httpClient;
    private readonly ILogger? _logger;
    private readonly IJsonSerializer _jsonSerializer;

    public string Name => "OriginalPoster";
    public int Order => 0;

    public OriginalPosterProvider(IHttpClient httpClient, ILogger logger, IJsonSerializer jsonSerializer)
    {
        _httpClient = httpClient;
        _logger = logger;
        _jsonSerializer = jsonSerializer;
        _logger?.Info("[OriginalPoster] Provider initialized with JsonSerializer");
    }

    public bool Supports(BaseItem item)
    {
        var supported = item is Movie || item is Series || item is Season || item is BoxSet;
        _logger?.Debug("[OriginalPoster] Supports check for {0}: {1}", item.Name ?? "Unknown", supported);
        return supported;
    }

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(
        BaseItem item,
        LibraryOptions libraryOptions,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            _logger?.Debug("[OriginalPoster] Configuration not found. Plugin disabled.");
            return Enumerable.Empty<RemoteImageInfo>();
        }

        if (!config.Enabled)
        {
            _logger?.Debug("[OriginalPoster] Please Enable Plugin");
            return Enumerable.Empty<RemoteImageInfo>();
        }

        _logger?.Debug("[OriginalPoster] GetImages called for: {0}", item.Name);

        if (config.TestMode)
        {
            return GetTestImage(config, libraryOptions);
        }

        var imagesTmdbId = GetTmdbId(item);
        if (string.IsNullOrWhiteSpace(imagesTmdbId))
        {
            _logger?.Debug("[OriginalPoster] No TMDB ID found for item, skipping");
            return Enumerable.Empty<RemoteImageInfo>();
        }

        if (string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            _logger?.Debug("[OriginalPoster] Please fill in TMDB API KEY.");
            return Enumerable.Empty<RemoteImageInfo>();
        }

        try
        {
            var tmdbClient = new TmdbClient(_httpClient, _jsonSerializer, config.TmdbApiKey);
            var detailsTmdbId = item is Season ? imagesTmdbId.Split("_S", StringSplitOptions.RemoveEmptyEntries)[0] : imagesTmdbId;
            var detailsType = GetDetailsType(item);
            var targetLanguage = await GetTargetLanguage(tmdbClient, detailsTmdbId, detailsType, cancellationToken).ConfigureAwait(false);

            _logger?.Debug("[OriginalPoster] Fetching images for TMDB ID: {0}, language: {1}", imagesTmdbId, targetLanguage);

            var imagesType = GetImagesType(item);
            var result = await tmdbClient.GetImagesAsync(imagesTmdbId, imagesType, targetLanguage, cancellationToken).ConfigureAwait(false);

            var allImages = new List<RemoteImageInfo>();
            allImages.AddRange(ConvertToRemoteImageInfo(
                result.posters, targetLanguage, libraryOptions,
                config.PosterSelectionStrategy, ImageType.Primary));

            if (config.EnableOriginalLogo)
            {
                allImages.AddRange(ConvertToRemoteImageInfo(
                    result.logos, targetLanguage, libraryOptions,
                    config.PosterSelectionStrategy, ImageType.Logo));
            }

            _logger?.Debug("[OriginalPoster] Fetched {0} images from TMDB", allImages.Count);
            return allImages;
        }
        catch (Exception ex)
        {
            _logger?.Error("[OriginalPoster] Failed to fetch images from TMDB for {0}. Error: {1}", item.Name, ex.Message);
            return Enumerable.Empty<RemoteImageInfo>();
        }
    }

    private IEnumerable<RemoteImageInfo> GetTestImage(OriginalPosterConfig config, LibraryOptions libraryOptions)
    {
        _logger?.Debug("[OriginalPoster] Test mode enabled, returning test poster");

        string testLangCode = !string.IsNullOrWhiteSpace(libraryOptions.PreferredMetadataLanguage)
            ? libraryOptions.PreferredMetadataLanguage.Trim()
            : "en";

        return new[]
        {
            new RemoteImageInfo
            {
                ProviderName = Name,
                Type = ImageType.Primary,
                Url = config.TestPosterUrl.Trim(),
                ThumbnailUrl = config.TestPosterUrl.Trim(),
                Language = testLangCode,
                DisplayLanguage = GetDisplayLanguage(testLangCode),
                Width = 2000,
                Height = 3000,
                CommunityRating = 10,
                VoteCount = 100,
                RatingType = RatingType.Score
            }
        };
    }

    private async Task<string> GetTargetLanguage(
        TmdbClient tmdbClient,
        string detailsTmdbId,
        string detailsType,
        CancellationToken cancellationToken)
    {
        const string fallbackLanguage = "en";

        var cacheManager = Plugin.Instance?.CacheManager;
        if (cacheManager != null && cacheManager.TryGetLanguage(detailsTmdbId, detailsType, out var cachedLang) && !string.IsNullOrWhiteSpace(cachedLang))
        {
            _logger?.Debug("[OriginalPoster] Cache HIT for {0}: {1}", detailsTmdbId, cachedLang);
            return cachedLang;
        }

        _logger?.Debug("[OriginalPoster] Cache MISS for {0}, fetching from TMDB...", detailsTmdbId);
        var details = await tmdbClient.GetItemDetailsAsync(detailsTmdbId, detailsType, cancellationToken).ConfigureAwait(false);
        var targetLanguage = ResolveTargetLanguage(details, fallbackLanguage);

        _logger?.Debug(
            "[OriginalPoster] Detected: original_language={0}, origin_country=[{1}], production_countries=[{2}] -> targetLanguage={3}",
            details.original_language,
            string.Join(",", details.origin_country),
            string.Join(",", details.production_countries.Select(p => p.iso_3166_1).Where(c => !string.IsNullOrWhiteSpace(c))),
            targetLanguage);

        cacheManager?.AddAndSave(detailsTmdbId, detailsType, targetLanguage);
        return targetLanguage;
    }

    private static string ResolveTargetLanguage(TmdbItemDetails details, string fallbackLanguage)
    {
        var originalLang = details.original_language?.Trim();
        if (string.Equals(originalLang, "cn", StringComparison.OrdinalIgnoreCase))
            originalLang = "zh";

        // TMDB groups many Chinese posters under zh, so keep zh unified for CN/HK/TW/SG/MO titles.
        if (!string.IsNullOrWhiteSpace(originalLang))
            return originalLang;

        var originCountry = details.origin_country.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
        if (!string.IsNullOrWhiteSpace(originCountry))
            return LanguageMapper.GetLanguageForCountry(originCountry);

        var productionCountry = details.production_countries
            .Select(p => p.iso_3166_1)
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

        return !string.IsNullOrWhiteSpace(productionCountry)
            ? LanguageMapper.GetLanguageForCountry(productionCountry)
            : fallbackLanguage;
    }

    private static string GetDetailsType(BaseItem item) => item switch
    {
        Movie => "movie",
        Series => "tv",
        Season => "tv",
        BoxSet => "collection",
        _ => "movie"
    };

    private static string GetImagesType(BaseItem item) => item switch
    {
        Movie => "movie",
        Series => "tv",
        Season => "tv_season",
        BoxSet => "collection",
        _ => "movie"
    };

    private string? GetTmdbId(BaseItem item)
    {
        if (item is BoxSet boxSet)
        {
            string? idString = null;
            if (boxSet.ProviderIds?.TryGetValue("TmdbCollection", out idString) != true)
            {
                boxSet.ProviderIds?.TryGetValue(MetadataProviders.Tmdb.ToString(), out idString);
            }

            if (!string.IsNullOrWhiteSpace(idString))
            {
                if (idString.StartsWith("collection/", StringComparison.OrdinalIgnoreCase))
                    idString = idString.Substring("collection/".Length);

                if (int.TryParse(idString, out _))
                {
                    _logger?.Debug("[OriginalPoster] Found BoxSet TMDB ID: {0}", idString);
                    return idString;
                }
            }

            _logger?.Debug("[OriginalPoster] BoxSet found, but no valid numeric TMDB ID in ProviderIds.");
            return null;
        }

        if (item is Movie || item is Series)
        {
            if (item.ProviderIds?.TryGetValue(MetadataProviders.Tmdb.ToString(), out var id) == true)
            {
                _logger?.Debug("[OriginalPoster] TMDB ID: {0}", id);
                return id;
            }
        }
        else if (item is Season season)
        {
            var series = season.Series;
            if (series?.ProviderIds?.TryGetValue(MetadataProviders.Tmdb.ToString(), out var seriesTmdbId) == true)
            {
                var seasonId = $"{seriesTmdbId}_S{season.IndexNumber}";
                _logger?.Debug("[OriginalPoster] Season composite TMDB ID: {0}", seasonId);
                return seasonId;
            }
        }

        return null;
    }

    private IEnumerable<RemoteImageInfo> ConvertToRemoteImageInfo(
        TmdbImage[]? images,
        string targetLanguage,
        LibraryOptions libraryOptions,
        PosterSelectionStrategy strategy,
        ImageType imageType)
    {
        if (images == null || images.Length == 0)
            return Array.Empty<RemoteImageInfo>();

        var sorted = images
            .Where(image => !string.IsNullOrWhiteSpace(image.file_path))
            .Select(image => new
            {
                Image = image,
                FilePath = image.file_path!,
                DisplayLang = image.iso_639_1 ?? targetLanguage,
                CalculatedRating = GetStrategyBasedRating(image, targetLanguage, strategy)
            })
            .OrderByDescending(x => x.CalculatedRating)
            .ThenByDescending(x => x.Image.vote_count);

        var result = sorted.Select(x => new RemoteImageInfo
        {
            ProviderName = Name,
            Type = imageType,
            Url = $"https://image.tmdb.org/t/p/original{x.FilePath}",
            ThumbnailUrl = $"https://image.tmdb.org/t/p/w500{x.FilePath}",
            Language = !string.IsNullOrWhiteSpace(libraryOptions.PreferredMetadataLanguage)
                ? libraryOptions.PreferredMetadataLanguage
                : x.DisplayLang,
            DisplayLanguage = GetDisplayLanguage(x.DisplayLang),
            Width = x.Image.width,
            Height = x.Image.height,
            CommunityRating = x.CalculatedRating,
            VoteCount = x.Image.vote_count,
            RatingType = RatingType.Score
        }).ToList();

        foreach (var entry in result.Take(3).Select((img, index) => new { img, index }))
        {
            _logger?.Debug("[OriginalPoster] Returned image #{0}: URL={1}, Language={2}, Rating={3}",
                entry.index + 1, entry.img.Url, entry.img.Language, entry.img.CommunityRating);
        }

        return result;
    }

    private static double GetStrategyBasedRating(TmdbImage image, string targetLanguage, PosterSelectionStrategy strategy)
    {
        double baseRating = image.vote_average;
        string? imageLanguage = image.iso_639_1;
        string targetLangBase = targetLanguage.Split('-')[0];

        return strategy switch
        {
            PosterSelectionStrategy.OriginalLanguageFirst when imageLanguage == targetLangBase => baseRating + 20,
            PosterSelectionStrategy.OriginalLanguageFirst when imageLanguage == null => baseRating + 10,
            PosterSelectionStrategy.NoTextPosterFirst when imageLanguage == null => baseRating + 20,
            PosterSelectionStrategy.NoTextPosterFirst when imageLanguage == targetLangBase => baseRating + 10,
            _ => baseRating
        };
    }

    private static string GetDisplayLanguage(string? langCode)
    {
        if (string.IsNullOrWhiteSpace(langCode))
            return "Unknown";

        try
        {
            var culture = new CultureInfo(langCode);
            return culture.EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return langCode.ToUpperInvariant();
        }
    }

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        return new[] { ImageType.Primary, ImageType.Logo };
    }

    public bool Supports(BaseItem item, ImageType imageType)
    {
        return (imageType == ImageType.Primary || imageType == ImageType.Logo) && Supports(item);
    }

    public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        _logger?.Info("[OriginalPoster] Emby selected image for download: {0}", url);

        return _httpClient.GetResponse(new HttpRequestOptions
        {
            Url = url,
            CancellationToken = cancellationToken
        });
    }
}
