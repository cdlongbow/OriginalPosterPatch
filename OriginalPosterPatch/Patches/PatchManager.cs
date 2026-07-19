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
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;

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
    private static bool _useIHasProviderIds;
    private static MethodInfo? _seasonImageMethod;
    private static MethodInfo? _addImageMethod;

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

            var allMethods = pm.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name == "GetAvailableRemoteImages").ToList();
            if (allMethods.Count == 0) { log.Warn("[OPPatch] GetAvailableRemoteImages not found"); return; }

            foreach (var m in allMethods)
            {
                var parms = m.GetParameters().Select(p => p.ParameterType.Name).ToList();
                log.Info("[OPPatch] Found GetAvailableRemoteImages({0})", string.Join(", ", parms));
                if (parms.Count >= 4 && parms[0] == "BaseItem" && parms[2] == "RemoteImageQuery")
                {
                    _getAvailableRemoteImages = m;
                    _useIHasProviderIds = false;
                    break;
                }
                if (parms.Count >= 4 && parms[0] == "IHasProviderIds" && parms[2] == "RemoteImageQuery")
                {
                    _getAvailableRemoteImages = m;
                    _useIHasProviderIds = true;
                    break;
                }
            }
            if (_getAvailableRemoteImages == null) { log.Warn("[OPPatch] GetAvailableRemoteImages signature not matched"); return; }

            var prefixMethod = _useIHasProviderIds
                ? new HarmonyMethod(typeof(PatchManager), nameof(PrefixIHasProviderIds))
                : new HarmonyMethod(typeof(PatchManager), nameof(PrefixBaseItem));
            var postfixMethod = _useIHasProviderIds
                ? new HarmonyMethod(typeof(PatchManager), nameof(PostfixIHasProviderIds))
                : new HarmonyMethod(typeof(PatchManager), nameof(PostfixBaseItem));

            _harmony.Patch(_getAvailableRemoteImages, prefixMethod, postfixMethod);
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

            var localMetadataAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Emby.LocalMetadata");
            if (localMetadataAssembly != null)
            {
                var localImageProvider = localMetadataAssembly.GetType("Emby.LocalMetadata.Images.LocalImageProvider", false);
                if (localImageProvider != null)
                {
                    _seasonImageMethod = localImageProvider.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(m =>
                        {
                            var parms = m.GetParameters();
                            if (parms.Length != 4) return false;
                            return parms[0].ParameterType == typeof(Season) &&
                                   parms[1].ParameterType == typeof(LibraryOptions) &&
                                   parms[2].ParameterType.IsGenericType &&
                                   parms[2].ParameterType.GetGenericTypeDefinition() == typeof(List<>) &&
                                   parms[3].ParameterType == typeof(IDirectoryService);
                        });

                    if (_seasonImageMethod != null)
                    {
                        _harmony.Patch(_seasonImageMethod, postfix: new HarmonyMethod(typeof(PatchManager), nameof(SeasonImagePostfix)));
                        log.Info("[OPPatch] LocalImageProvider season image method patched");

                        _addImageMethod = localImageProvider.GetMethod("AddImage",
                            BindingFlags.Instance | BindingFlags.Public, null,
                            new[] { typeof(FileSystemMetadata[]), typeof(List<LocalImageInfo>), typeof(string), typeof(ImageType) },
                            null);
                        if (_addImageMethod == null)
                            log.Warn("[OPPatch] LocalImageProvider.AddImage not found");
                    }
                    else
                    {
                        log.Warn("[OPPatch] LocalImageProvider season image method not found");
                    }
                }
                else
                {
                    log.Warn("[OPPatch] Emby.LocalMetadata.Images.LocalImageProvider type not found");
                }
            }
            else
            {
                log.Warn("[OPPatch] Emby.LocalMetadata assembly not found");
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
            if (movieData == null)
            {
                _log?.Info("[OPPatch] GetMovieInfoPostfix: movieData is null for {0}", item.Name);
                return;
            }

            var allProps = movieData.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(p => p.Name).ToArray();
            _log?.Info("[OPPatch] GetMovieInfoPostfix: props={0}", string.Join(",", allProps));

            var idProp = movieData.GetType().GetProperty("id");
            var langProp = movieData.GetType().GetProperty("original_language",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (idProp == null || langProp == null)
            {
                _log?.Info("[OPPatch] GetMovieInfoPostfix: idProp={0}, langProp={1}", idProp != null, langProp != null);
                return;
            }

            var id = idProp.GetValue(movieData)?.ToString();
            var origLang = langProp.GetValue(movieData)?.ToString();
            _log?.Info("[OPPatch] GetMovieInfoPostfix: id={0}, lang={1}", id ?? "null", origLang ?? "null");

            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(origLang))
            {
                var normalized = origLang.Trim().ToLowerInvariant();
                OriginalLanguageByTmdbId[id] = normalized == "cn" ? "zh" : normalized;
                _log?.Info("[OPPatch] Movie original_language {0} -> {1}", id, normalized);
            }
        }
        catch (Exception ex)
        {
            _log?.Info("[OPPatch] GetMovieInfoPostfix error: {0}", ex.Message);
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

    [HarmonyPostfix]
    private static void SeasonImagePostfix(object __instance,
        [HarmonyArgument(0)] Season season,
        [HarmonyArgument(1)] LibraryOptions libraryOptions,
        [HarmonyArgument(2)] List<LocalImageInfo> images,
        [HarmonyArgument(3)] IDirectoryService directoryService)
    {
        try
        {
            if (season.IndexNumber == null || season.IndexNumber.Value != 0) return;
            if (string.IsNullOrWhiteSpace(season.Path)) return;

            var files = directoryService.GetFiles(season.Path);
            if (files == null || files.Count == 0) return;

            _addImageMethod?.Invoke(__instance, new object[] { files, images, "season00-poster", ImageType.Primary });
        }
        catch (Exception ex)
        {
            _log?.Debug("[OPPatch] SeasonImagePostfix: {0}", ex.Message);
        }
    }

    [HarmonyPrefix]
    private static void PrefixBaseItem(BaseItem item, LibraryOptions libraryOptions, ref RemoteImageQuery query,
        CancellationToken cancellationToken)
    {
        DoPrefix(item, ref query);
    }

    [HarmonyPrefix]
    private static void PrefixIHasProviderIds(MediaBrowser.Model.Entities.IHasProviderIds item,
        LibraryOptions libraryOptions, ref RemoteImageQuery query,
        CancellationToken cancellationToken)
    {
        DoPrefix(item as BaseItem, ref query);
    }

    private static void DoPrefix(BaseItem? item, ref RemoteImageQuery query)
    {
        if (!_enabled || item == null || query == null) return;
        query.IncludeAllLanguages = true;
        var tmdbId = item.GetProviderId(MetadataProviders.Tmdb);
        var imdbId = item.GetProviderId(MetadataProviders.Imdb);
        CurrentContext.Value = new ContextItem { TmdbId = tmdbId, ImdbId = imdbId };
    }

    [HarmonyPostfix]
    private static void PostfixBaseItem(BaseItem item, LibraryOptions libraryOptions, RemoteImageQuery query,
        CancellationToken cancellationToken,
        ref Task<IEnumerable<RemoteImageInfo>> __result)
    {
        DoPostfix(item, libraryOptions, ref __result);
    }

    [HarmonyPostfix]
    private static void PostfixIHasProviderIds(MediaBrowser.Model.Entities.IHasProviderIds item,
        LibraryOptions libraryOptions, RemoteImageQuery query,
        CancellationToken cancellationToken,
        ref Task<IEnumerable<RemoteImageInfo>> __result)
    {
        DoPostfix(item as BaseItem, libraryOptions, ref __result);
    }

    private static void DoPostfix(BaseItem? item, LibraryOptions libraryOptions,
        ref Task<IEnumerable<RemoteImageInfo>> __result)
    {
        if (!_enabled || item == null || __result == null) return;

        var prefLang = libraryOptions.PreferredImageLanguage;
        if (string.IsNullOrWhiteSpace(prefLang)) prefLang = "en";

        var origLang = ResolveOriginalLanguage(item);
        var itemName = item.Name;
        var logPref = prefLang;
        var logOrig = origLang ?? "null";

        __result = __result.ContinueWith(task =>
        {
            try
            {
                var imgs = task.Result?.ToList();
                if (imgs == null || imgs.Count == 0) return Enumerable.Empty<RemoteImageInfo>();

                var langs = imgs.GroupBy(i => i.Language ?? "(none)")
                    .Select(g => $"{g.Key}:{g.Count()}");
                _log?.Info("[OPPatch] item={0}, pref={1}, orig={2}, before: {3}",
                    itemName, logPref, logOrig, string.Join(", ", langs));

                var sorted = Sort(imgs, logPref, logOrig).ToList();

                var top5 = sorted.Take(5).Select(i => $"{i.Language ?? "(none)"}({i.CommunityRating})");
                _log?.Info("[OPPatch] after sort top5: {0}", string.Join(" > ", top5));

                return sorted;
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
            return baseScore + 0;

        if (_zhSplit && isZh)
        {
            if (img.Language?.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase) == true ||
                img.Language?.StartsWith("zh-SG", StringComparison.OrdinalIgnoreCase) == true)
                return baseScore + 100;
            if (img.Language?.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) == true ||
                img.Language?.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) == true)
                return baseScore + 80;
        }

        if (lang == prefBase) return baseScore + 60;
        if (!string.IsNullOrWhiteSpace(origBase) && lang == origBase) return baseScore + 30;
        if (lang == "en") return baseScore + 10;

        return baseScore + 5;
    }
}