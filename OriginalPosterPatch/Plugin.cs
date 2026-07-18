using System;
using System.IO;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using OriginalPosterPatch.Patches;

namespace OriginalPosterPatch;

public class Plugin : BasePluginSimpleUI<Config>, IHasThumbImage
{
    public static Plugin? Instance { get; private set; }

    public override Guid Id => new Guid("A3B7C1D2-E5F6-7890-ABCD-EF1234567890");
    public override string Name => "Original Poster Patch";
    public override string Description => "优先首选语言海报，无则回退原语言海报";

    public Plugin(IApplicationHost appHost, ILogManager logManager) : base(appHost)
    {
        Instance = this;
        var log = logManager.GetLogger(Name);
        log.Info("[OPPatch] 加载中");
        PatchManager.Init(log);
        PatchManager.Configure(GetOptions().Enabled, GetOptions().EnableZhSplit);
    }

    public Stream GetThumbImage()
    {
        var s = GetType().Assembly.GetManifestResourceStream("OriginalPosterPatch.Properties.thumb.png");
        return s ?? throw new InvalidOperationException("thumb.png not found");
    }

    public ImageFormat ThumbImageFormat => ImageFormat.Png;

    protected override void OnOptionsSaved(Config options)
    {
        base.OnOptionsSaved(options);
        PatchManager.Configure(options.Enabled, options.EnableZhSplit);
    }
}