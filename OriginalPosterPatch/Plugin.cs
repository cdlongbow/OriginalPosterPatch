using System;
using System.IO;
using System.Reflection;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using OriginalPosterPatch.Patches;

namespace OriginalPosterPatch;

public class Plugin : BasePluginSimpleUI<Config>, IHasThumbImage
{
    public static Plugin? Instance { get; private set; }
    private readonly ILogger _log;

    static Plugin()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var name = new AssemblyName(args.Name).Name;
            if (name != "0Harmony") return null;
            var entryDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location);
            if (entryDir == null) return null;
            var candidates = new[]
            {
                Path.Combine(entryDir, "0Harmony.dll"),
                Path.Combine(entryDir, "plugins", "0Harmony.dll"),
                Path.Combine(entryDir, "..", "plugins", "0Harmony.dll"),
                Path.Combine(entryDir, "..", "programdata", "plugins", "0Harmony.dll"),
            };
            foreach (var c in candidates)
            {
                var path = Path.GetFullPath(c);
                if (File.Exists(path))
                    return Assembly.LoadFrom(path);
            }
            return null;
        };
    }

    public override Guid Id => new Guid("A3B7C1D2-E5F6-7890-ABCD-EF1234567890");
    public override string Name => "Original Poster Patch";
    public override string Description => "优先首选语言海报，无则回退原语言海报";

    public Plugin(IApplicationHost appHost, ILogManager logManager) : base(appHost)
    {
        Instance = this;
        _log = logManager.GetLogger(Name);
        _log.Info("[OPPatch] 加载中");

        try
        {
            PatchManager.Init(_log);
            PatchManager.Configure(GetOptions().Enabled, GetOptions().EnableZhSplit);
        }
        catch (Exception ex)
        {
            _log.Error("[OPPatch] Harmony init failed: {0}", ex.Message);
        }
    }

    protected override void OnOptionsSaved(Config options)
    {
        base.OnOptionsSaved(options);
        PatchManager.Configure(options.Enabled, options.EnableZhSplit);
    }

    public Stream GetThumbImage()
    {
        var s = GetType().Assembly.GetManifestResourceStream("OriginalPosterPatch.Properties.thumb.png");
        return s ?? throw new InvalidOperationException("thumb.png not found");
    }

    public ImageFormat ThumbImageFormat => ImageFormat.Png;
}