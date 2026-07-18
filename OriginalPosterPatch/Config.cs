using System.Text.Json.Serialization;

namespace OriginalPosterPatch;

public class Config
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("enable_zh_split")]
    public bool EnableZhSplit { get; set; } = false;
}