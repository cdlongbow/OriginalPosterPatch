using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;

namespace OriginalPosterPatch.Patches;

public static class PatchManager
{
    private static Harmony? _harmony;
    private static ILogger? _log;
    private static bool _enabled;
    private static bool _zhSplit;

    private static Assembly? _movieDbAssembly;
    private static MethodInfo? _getMovieInfo;
    private static MethodInfo? _ensureSeriesInfo;
    private static MethodInfo? _getAvailableRemoteImages;

    private static readonly ConcurrentDictionary<string, string> OriginalLanguageByTmdbId = new();
    private static readonly AsyncLocal<ContextItem?> CurrentContext = new();

    private class ContextItem
    {
        public string? TmdbId { get; set; }
        public string? ImdbId { get; set; }
    }

    public static void Init(ILogger log)
    {
        _log = log;
        try
        {
            _harmony = new Harmony("oppatch.image");

            var embyProviders = Assembly.Load("Emby.Providers");
            var pm = embyProviders.GetType("Emby.Providers.Manager.ProviderManager", false);
            if (pm == null) { log.Warn("[OPPatch] ProviderManager not found"); return; }

            _getAvailableRemoteImages = pm.GetMethod("GetAvailableRemoteImages",
                BindingFlags.Instance | BindingFlags.Public, null,
                new[] { typeof(BaseItem), typeof(LibraryOptions), typeof(RemoteImageQuery),
                    typeof(IDirectoryService), typeof(CancellationToken) }, null);
            if (_getAvailableRemoteImages == null) { log.Warn("[OPPatch] GetAvailableRemoteImages not found"); return; }

            _harmony.Patch(_getAvailableRemoteImages,
                new HarmonyMethod(typeof(PatchManager), nameof(Prefix)),
                new HarmonyMethod(typeof(PatchManager), nameof(Postfix)));
            log.Info("[OPPatch] GetAvailableRemoteImages patched");

            _movieDbAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "MovieDb");
            if (_movieDbAssembly != null)
            {
                var imageProvider = _movieDbAssembly.GetType("MovieDb.MovieDbImageProvider", false);
                if (imageProvider != null)
                {
                    _getMovieInfo = imageProvider.GetMethod("GetMovieInfo",
                        BindingFlags.Instance | BindingFlags.NonPublic, null,
                        new[] { typeof(BaseItem), typeof(string), typeof(IJsonSerializer), typeof(CancellationToken) },
                        null);
                    if (_getMovieInfo != null)
                    {
                        _harmony.Patch(_getMovieInfo, postfix: new HarmonyMethod(typeof(PatchManager), nameof(GetMovieInfoPostfix)));
                        log.Info("[OPPatch] MovieDbImageProvider.GetMovieInfo patched");
                    }
                }

                var seriesProvider = _movieDbAssembly.GetType("MovieDb.MovieDbSeriesProvider", false);
                if (seriesProvider != null)
                {
                    _ensureSeriesInfo = seriesProvider.GetMethod("EnsureSeriesInfo",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    if (_ensureSeriesInfo != null)
                    {
                        _harmony.Patch(_ensureSeriesInfo, postfix: new HarmonyMethod(typeof(PatchManager), nameof(EnsureSeriesInfoPostfix)));
                        log.Info("[OPPatch] MovieDbSeriesProvider.EnsureSeriesInfo patched");
                    }
                }
            }
            else
            {
                log.Warn("[OPPatch] MovieDb assembly not found, original language detection unavailable");
            }
        }
        catch (Exception ex)
        {
            log.Error("[OPPatch] Init: {0}", ex.Message);
        }
    }

    public static void Configure(bool enabled, bool zhSplit)
    {
        _enabled = enabled;
        _zhSplit = zhSplit;
        _log?.Info("[OPPatch] Configured: enabled={0}, zhSplit={1}", enabled, zhSplit);
    }

    private static string? ResolveOriginalLanguage(BaseItem item)
    {
        if (item is Season s) return ResolveOriginalLanguage(s.Series);
        if (item is Episode ep) return ResolveOriginalLanguage(ep.Series);
        if (item is not Movie && item is not Series) return null;

        var tmdbId = item.GetProviderId(MetadataProviders.Tmdb);
        if (string.IsNullOrWhiteSpace(tmdbId)) return null;

        if (OriginalLanguageByTmdbId.TryGetValue(tmdbId, out var lang))
            return lang;

        return null;
    }

    [HarmonyPostfix]
    private static void GetMovieInfoPostfix(BaseItem item, string language, IJsonSerializer jsonSerializer,
        CancellationToken cancellationToken, Task __result)
    {
        try
        {
            var resultProp = __result.GetType().GetProperty("Result");
            var movieData = resultProp?.GetValue(__result);
            if (movieData == null) return;

            var idProp = movieData.GetType().GetProperty("id");
            var langProp = movieData.GetType().GetProperty("original_language",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (idProp == null || langProp == null) return;

            var id = idProp.GetValue(movieData)?.ToString();
            var origLang = langProp.GetValue(movieData)?.ToString();
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(origLang))
            {
                var normalized = origLang.Trim().ToLowerInvariant();
                OriginalLanguageByTmdbId[id] = normalized == "cn" ? "zh" : normalized;
                _log?.Debug("[OPPatch] Movie original_language {0} -> {1}", id, normalized);
            }
        }
        catch (Exception ex)
        {
            _log?.Debug("[OPPatch] GetMovieInfoPostfix: {0}", ex.Message);
        }
    }

    [HarmonyPostfix]
    private static void EnsureSeriesInfoPostfix(string tmdbId, string language,
        CancellationToken cancellationToken, Task __result)
    {
        try
        {
            var resultProp = __result.GetType().GetProperty("Result");
            var seriesInfo = resultProp?.GetValue(__result);
            if (seriesInfo == null) return;

            var languagesProp = seriesInfo.GetType().GetProperty("languages",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (languagesProp == null) return;

            var languages = languagesProp.GetValue(seriesInfo) as List<string>;
            var origLang = languages?.FirstOrDefault();
            if (!string.IsNullOrEmpty(origLang))
            {
                var normalized = origLang.Trim().ToLowerInvariant();
                OriginalLanguageByTmdbId[tmdbId] = normalized == "cn" ? "zh" : normalized;
                _log?.Debug("[OPPatch] Series original_language {0} -> {1}", tmdbId, normalized);
            }
        }
        catch (Exception ex)
        {
            _log?.Debug("[OPPatch] EnsureSeriesInfoPostfix: {0}", ex.Message);
        }
    }

    [HarmonyPrefix]
    private static void Prefix(BaseItem item, LibraryOptions libraryOptions, ref RemoteImageQuery query,
        IDirectoryService directoryService, CancellationToken cancellationToken)
    {
        if (!_enabled || item == null || query == null) return;
        query.IncludeAllLanguages = true;

        var tmdbId = item.GetProviderId(MetadataProviders.Tmdb);
        var imdbId = item.GetProviderId(MetadataProviders.Imdb);
        CurrentContext.Value = new ContextItem { TmdbId = tmdbId, ImdbId = imdbId };
    }

    [HarmonyPostfix]
    private static void Postfix(BaseItem item, LibraryOptions libraryOptions, RemoteImageQuery query,
        IDirectoryService directoryService, CancellationToken cancellationToken,
        ref Task<IEnumerable<RemoteImageInfo>> __result)
    {
        if (!_enabled || item == null || __result == null) return;

        var prefLang = libraryOptions.PreferredImageLanguage;
        if (string.IsNullOrWhiteSpace(prefLang)) prefLang = "en";

        var origLang = ResolveOriginalLanguage(item);

        __result = __result.ContinueWith(task =>
        {
            try
            {
                var imgs = task.Result;
                if (imgs == null) return Enumerable.Empty<RemoteImageInfo>();
                return Sort(imgs, prefLang, origLang);
            }
            catch
            {
                return task.Result ?? Enumerable.Empty<RemoteImageInfo>();
            }
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private static IEnumerable<RemoteImageInfo> Sort(IEnumerable<RemoteImageInfo> imgs,
        string prefLang, string? origLang)
    {
        var prefBase = prefLang.Split('-')[0].ToLowerInvariant();
        var origBase = origLang?.Split('-')[0].ToLowerInvariant();
        var isZh = prefBase == "zh";

        return imgs
            .Select(x => new { Img = x, Score = CalcScore(x, prefBase, origBase, isZh) })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Img.CommunityRating ?? 0)
            .Select(x => x.Img);
    }

    private static double CalcScore(RemoteImageInfo img, string prefBase, string? origBase, bool isZh)
    {
        var lang = img.Language?.Split('-')[0].ToLowerInvariant();
        var baseScore = img.CommunityRating ?? 0;

        if (string.IsNullOrWhiteSpace(lang))
            return baseScore + 5;

        if (_zhSplit && isZh)
        {
            if (img.Language?.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase) == true ||
                img.Language?.StartsWith("zh-SG", StringComparison.OrdinalIgnoreCase) == true)
                return baseScore + 30;
            if (img.Language?.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) == true ||
                img.Language?.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) == true)
                return baseScore + 25;
        }

        if (lang == prefBase) return baseScore + 20;
        if (!string.IsNullOrWhiteSpace(origBase) && lang == origBase) return baseScore + 15;

        return baseScore + 10;
    }
}