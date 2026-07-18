using System;
using System.IO;
using System.Text.Json;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using OriginalPosterPatch.Patches;

namespace OriginalPosterPatch;

public class Plugin : BasePlugin, IHasThumbImage
{
    public static Plugin? Instance { get; private set; }
    private readonly ILogger _log;
    private Config _config = new();

    public override Guid Id => new Guid("A3B7C1D2-E5F6-7890-ABCD-EF1234567890");
    public override string Name => "Original Poster Patch";
    public override string Description => "优先首选语言海报，无则回退原语言海报";

    public Plugin(IApplicationHost appHost, ILogManager logManager)
    {
        Instance = this;
        _log = logManager.GetLogger(Name);
        _log.Info("[OPPatch] 加载中");
        LoadConfig();
        PatchManager.Init(_log);
        PatchManager.Configure(_config.Enabled, _config.EnableZhSplit);
    }

    private string ConfigPath => Path.Combine(DataFolderPath, "config.json");

    private void LoadConfig()
    {
        try
        {
            var path = ConfigPath;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                _config = JsonSerializer.Deserialize<Config>(json) ?? new Config();
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
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
            PatchManager.Configure(_config.Enabled, _config.EnableZhSplit);
        }
        catch (Exception ex)
        {
            _log.Error("[OPPatch] SaveConfig: {0}", ex.Message);
        }
    }

    public Config GetConfig() => _config;

    public Stream GetThumbImage()
    {
        var s = GetType().Assembly.GetManifestResourceStream("OriginalPosterPatch.Properties.thumb.png");
        return s ?? throw new InvalidOperationException("thumb.png not found");
    }

    public ImageFormat ThumbImageFormat => ImageFormat.Png;
}