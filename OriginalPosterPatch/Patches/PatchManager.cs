using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;

namespace OriginalPosterPatch.Patches;

public static class PatchManager
{
    private static Harmony? _harmony;
    private static ILogger? _log;
    private static bool _enabled;
    private static bool _zhSplit;

    // MovieDb 反射成员
    private static PropertyInfo? _movieDbCurrent;
    private static PropertyInfo? _seriesDbCurrent;
    private static MethodInfo? _ensureMovieInfo;
    private static MethodInfo? _ensureSeriesInfo;
    private static MethodInfo? _getImages;

    public static void Init(ILogger log)
    {
        _log = log;
        try
        {
            _harmony = new Harmony("oppatch.image");

            var embyProviders = Assembly.Load("Emby.Providers");
            var pm = embyProviders.GetType("Emby.Providers.Manager.ProviderManager", false);
            if (pm == null) { log.Warn("[OPPatch] ProviderManager not found"); return; }

            _getImages = pm.GetMethod("GetAvailableRemoteImages",
                BindingFlags.Instance | BindingFlags.Public, null,
                new[] { typeof(BaseItem), typeof(LibraryOptions), typeof(RemoteImageQuery), typeof(System.Threading.CancellationToken) }, null);
            if (_getImages == null) { log.Warn("[OPPatch] GetAvailableRemoteImages not found"); return; }

            _harmony.Patch(_getImages,
                new HarmonyMethod(typeof(PatchManager), nameof(Prefix)),
                new HarmonyMethod(typeof(PatchManager), nameof(Postfix)));
            log.Info("[OPPatch] GetAvailableRemoteImages patched");

            // 解析内置 MovieDb 提供者
            var movieDb = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "MovieDb");
            if (movieDb != null)
            {
                var mp = movieDb.GetType("MovieDb.MovieDbProvider", false);
                var sp = movieDb.GetType("MovieDb.MovieDbSeriesProvider", false);
                _movieDbCurrent = mp?.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                _seriesDbCurrent = sp?.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                _ensureMovieInfo = mp?.GetMethod("EnsureMovieInfo", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(string), typeof(string), typeof(System.Threading.CancellationToken) }, null);
                _ensureSeriesInfo = sp?.GetMethod("EnsureSeriesInfo", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(string), typeof(string), typeof(System.Threading.CancellationToken) }, null);
                log.Info("[OPPatch] MovieDb resolved");
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

    private static string? GetOriginalLang(BaseItem item)
    {
        try
        {
            var target = item switch
            {
                Season s => s.Series,
                Episode ep => ep.Series,
                _ => item
            };
            if (target == null) return null;

            var tmdbId = target.GetProviderId(MetadataProviders.Tmdb);
            if (string.IsNullOrWhiteSpace(tmdbId)) return null;

            var isMovie = target is Movie;
            var method = isMovie ? _ensureMovieInfo : _ensureSeriesInfo;
            var prop = isMovie ? _movieDbCurrent : _seriesDbCurrent;
            if (method == null || prop == null) return null;

            var provider = prop.GetValue(null);
            var task = method.Invoke(provider, new object[] { tmdbId.Trim(), null, System.Threading.CancellationToken.None }) as Task;
            task?.GetAwaiter().GetResult();
            if (task == null) return null;

            var result = task.GetType().GetProperty("Result")?.GetValue(task);
            if (result == null) return null;

            var lang = result.GetType()
                .GetProperty("original_language", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)
                ?.GetValue(result)?.ToString();
            if (string.IsNullOrWhiteSpace(lang)) return null;

            lang = lang.Trim().ToLowerInvariant();
            return lang == "cn" ? "zh" : lang;
        }
        catch (Exception ex)
        {
            _log?.Debug("[OPPatch] GetOriginalLang: {0}", ex.Message);
            return null;
        }
    }

    [HarmonyPrefix]
    private static void Prefix(BaseItem item, ref LibraryOptions libraryOptions, ref RemoteImageQuery query)
    {
        if (!_enabled || item == null || query == null) return;
        query.IncludeAllLanguages = true;
    }

    [HarmonyPostfix]
    private static void Postfix(BaseItem item, LibraryOptions libraryOptions,
        ref Task<IEnumerable<RemoteImageInfo>> __result)
    {
        if (!_enabled || item == null || __result == null) return;

        var prefLang = libraryOptions.PreferredImageLanguage;
        if (string.IsNullOrWhiteSpace(prefLang)) prefLang = "en";
        var origLang = GetOriginalLang(item);

        __result = __result.ContinueWith(task =>
        {
            try
            {
                var imgs = task.Result;
                if (imgs == null) return Enumerable.Empty<RemoteImageInfo>();
                return Sort(imgs, prefLang, origLang);
            }
            catch { return task.Result ?? Enumerable.Empty<RemoteImageInfo>(); }
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

        // 无文字海报
        if (string.IsNullOrWhiteSpace(lang)) return baseScore + 5;

        // 简繁区分
        if (_zhSplit && isZh)
        {
            if (img.Language?.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase) == true ||
                img.Language?.StartsWith("zh-SG", StringComparison.OrdinalIgnoreCase) == true)
                return baseScore + 30;
            if (img.Language?.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) == true ||
                img.Language?.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) == true)
                return baseScore + 25;
        }

        // 首选语言
        if (lang == prefBase) return baseScore + 20;
        // 原语言
        if (!string.IsNullOrWhiteSpace(origBase) && lang == origBase) return baseScore + 15;

        return baseScore + 10;
    }
}