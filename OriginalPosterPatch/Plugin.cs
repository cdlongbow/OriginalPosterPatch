using System;
using System.IO;
using System.Text.Json;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using OriginalPosterPatch.Patches;

namespace OriginalPosterPatch;

public class Plugin : BasePlugin<BasePluginConfiguration>, IHasThumbImage
{
    public static Plugin? Instance { get; private set; }
    private readonly ILogger _log;
    private readonly IApplicationPaths _appPaths;
    private Config _config = new();
    private bool _configLoaded;

    public override Guid Id => new Guid("A3B7C1D2-E5F6-7890-ABCD-EF1234567890");
    public override string Name => "Original Poster Patch";
    public override string Description => "优先首选语言海报，无则回退原语言海报";

    public Plugin(IApplicationPaths appPaths, IXmlSerializer serializer, ILogManager logManager) : base(appPaths, serializer)
    {
        _appPaths = appPaths;
        Instance = this;
        _log = logManager.GetLogger(Name);
        _log.Info("[OPPatch] 加载中");

        TryLoadConfig();

        PatchManager.Init(_log);
        PatchManager.Configure(_config.Enabled, _config.EnableZhSplit);
    }

    private string ConfigPath => Path.Combine(_appPaths.PluginConfigurationsPath, Name, "config.json");

    private void TryLoadConfig()
    {
        try
        {
            var path = ConfigPath;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                _config = JsonSerializer.Deserialize<Config>(json) ?? new Config();
                _configLoaded = true;
            }
        }
        catch (Exception ex)
        {
            _log.Error("[OPPatch] LoadConfig: {0}", ex.Message);
        }
    }

    public void SaveConfig(Config config)
    {
        _config = config;
        _configLoaded = true;
        try
        {
            var path = ConfigPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            PatchManager.Configure(_config.Enabled, _config.EnableZhSplit);
        }
        catch (Exception ex)
        {
            _log.Error("[OPPatch] SaveConfig: {0}", ex.Message);
        }
    }

    public Config GetConfig()
    {
        if (!_configLoaded) TryLoadConfig();
        return _config;
    }

    public Stream GetThumbImage()
    {
        var s = GetType().Assembly.GetManifestResourceStream("OriginalPosterPatch.Properties.thumb.png");
        return s ?? throw new InvalidOperationException("thumb.png not found");
    }

    public ImageFormat ThumbImageFormat => ImageFormat.Png;
}