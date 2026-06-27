// Models/TmdbModels.cs
namespace OriginalPoster.Models;

/// <summary>
/// TMDB 项目详情（电影/剧集），用于获取 production_countries
/// </summary>
public class TmdbItemDetails
{
    public string? id { get; set; }
    public string? original_language { get; set; }
    public string[] origin_country { get; set; } = [];
    public ProductionCountry[] production_countries { get; set; } = [];

    public string? name { get; set; }
    public string? original_name { get; set; }
    public string? poster_path { get; set; }
    public string? backdrop_path { get; set; }
}

/// <summary>
/// 制片国家信息
/// </summary>
public class ProductionCountry
{
    /// <summary>
    /// ISO 3166-1 国家代码，如 "US", "CN"
    /// </summary>
    public string? iso_3166_1 { get; set; }
    public string? name { get; set; }
}

/// <summary>
/// TMDB 图像响应结果（/images 接口）
/// </summary>
public class TmdbImageResult
{
    /// <summary>
    /// 海报列表
    /// </summary>
    public TmdbImage[] posters { get; set; } = [];
    public TmdbImage[] logos { get; set; } = [];
    public TmdbImage[] backdrops { get; set; } = [];
}

/// <summary>
/// 单张图像信息
/// </summary>
public class TmdbImage
{
    /// <summary>
    /// 图像路径，如 "/abc123.jpg"
    /// </summary>
    public string? file_path { get; set; }

    public int width { get; set; }
    public int height { get; set; }

    /// <summary>
    /// 图像语言代码（可能为 null，表示无文字）
    /// </summary>
    public string? iso_639_1 { get; set; }
    public double vote_average { get; set; }
    public int vote_count { get; set; }
}
