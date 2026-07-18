using System;
using System.ComponentModel;
using Emby.Web.GenericEdit;

namespace OriginalPosterPatch;

public class Config : EditableOptionsBase
{
    public override string EditorTitle => "原语言海报补丁";

    [DisplayName("启用")]
    [Description("开启后优先首选语言海报，没有再获取原语言海报")]
    public bool Enabled { get; set; } = true;

    [DisplayName("启用简繁区分")]
    [Description("区分简体中文和繁体中文海报")]
    public bool EnableZhSplit { get; set; } = false;
}