using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace OriginalPoster.Services;

public class LanguageCacheManager
{
    private const string CacheKeyVersion = "v2";

    private readonly IJsonSerializer _jsonSerializer;
    private readonly string _cacheFilePath;
    private readonly object _writeLock = new object();
    private Dictionary<string, string> _cache;

    public LanguageCacheManager(IApplicationPaths applicationPaths, IJsonSerializer jsonSerializer)
    {
        _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
        _cacheFilePath = Path.Combine(applicationPaths.PluginConfigurationsPath, "OriginalPoster.Cache.json");
        _cache = LoadCache();
    }

    public bool TryGetLanguage(string tmdbId, string type, out string? language)
    {
        string key = GetKey(tmdbId, type);
        lock (_writeLock)
        {
            return _cache.TryGetValue(key, out language);
        }
    }

    public void AddAndSave(string tmdbId, string type, string language)
    {
        if (string.IsNullOrWhiteSpace(tmdbId) || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(language))
            return;

        string key = GetKey(tmdbId, type);

        lock (_writeLock)
        {
            if (_cache.TryGetValue(key, out var existing) && string.Equals(existing, language, StringComparison.OrdinalIgnoreCase))
                return;

            _cache[key] = language;
            SaveCache();
        }
    }

    public void Clear()
    {
        lock (_writeLock)
        {
            _cache.Clear();
            SaveCache();
        }
    }

    private static string GetKey(string tmdbId, string type) => $"{CacheKeyVersion}:{type}:{tmdbId}";

    private Dictionary<string, string> LoadCache()
    {
        try
        {
            if (!File.Exists(_cacheFilePath))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var json = File.ReadAllText(_cacheFilePath);
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var loadedCache = _jsonSerializer.DeserializeFromString<Dictionary<string, string>>(json);
            return loadedCache != null
                ? new Dictionary<string, string>(loadedCache, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OriginalPoster] Failed to load language cache: {ex.Message}");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveCache()
    {
        try
        {
            var directory = Path.GetDirectoryName(_cacheFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var tempPath = _cacheFilePath + ".tmp";
            var json = _jsonSerializer.SerializeToString(_cache);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _cacheFilePath, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OriginalPoster] Failed to save language cache: {ex.Message}");
        }
    }
}
