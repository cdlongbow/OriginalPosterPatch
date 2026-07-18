# Original Poster Patch

> **Emby Plugin** — Harmony Patch 方式实现原语言海报优先排序，无需独立 TMDB API Key。

## 功能

| 功能 | 说明 |
|------|------|
| 首选语言优先 | 优先显示媒体库设置的「首选图像下载语言」的海报，如简中 |
| 原语言回退 | 首选语言无图时，自动回退到 TMDB 原语言海报 |
| 简繁区分（尝鲜版） | 简体中文（zh-CN）优先于繁体中文（zh-TW/zh-HK）非精确，仅限尝鲜，勿喷 |
| 零配置 | 无需 TMDB API Key，复用 Emby 内置 MovieDb 提供者 |
| 全类型支持 | 电影 / 剧集 / 播出季 / 单集 / 合集 |

## 编译

### 本地编译（Windows）

```powershell
# 安装 .NET 8 SDK（如未安装）
# 下载：https://dotnet.microsoft.com/download/dotnet/8.0

# 克隆并编译
git clone -b OriginalPosterPatch https://github.com/cdlongbow/OriginalPoster.git
cd OriginalPoster
dotnet build OriginalPosterPatch/OriginalPosterPatch.csproj -c Release
```

编译产物在 `OriginalPosterPatch/bin/Release/net8.0/` 目录下，将 `OriginalPosterPatch.dll`（ILRepack 合并后的单文件）放入 Emby 的 `plugins` 目录即可。

> Windows 环境下编译会自动触发 ILRepack 将 `0Harmony.dll` 合并到输出 DLL。如在其他平台编译，需要手动将 `0Harmony.dll` 和 `OriginalPosterPatch.dll` 一起放入 plugins 目录。

### 依赖说明

| 包 | 版本 | 说明 |
|----|------|------|
| `Lib.Harmony` | 2.3.6 | Harmony 运行时库，用于运行时方法补丁 |
| `MediaBrowser.Common` | 4.9.1.90 | Emby 公共库（含 `BasePluginSimpleUI`） |
| `MediaBrowser.Server.Core` | 4.9.1.90 | Emby 服务端核心库（含 `BasePluginSimpleUI` 在 `MediaBrowser.Controller` 中） |
| `ILRepack` | 2.0.42 | 程序集合并工具，将 `0Harmony.dll` 合并到输出 |

### 常见编译问题

**`BasePluginSimpleUI` 找不到**
- 该类在 `MediaBrowser.Controller.dll` 的 `MediaBrowser.Controller.Plugins` 命名空间下
- NuGet 包 `MediaBrowser.Server.Core` 中已包含，无需额外引用

**`0Harmony.dll` 加载失败**
- 插件运行时需 `0Harmony.dll` 在 plugins 目录或 Emby 系统目录下
- 推荐在 Windows 编译，ILRepack 会自动合并到输出，部署只需一个 DLL
- 如在其他平台编译，需手动复制 `0Harmony.dll` 到 plugins 目录

**`GetAvailableRemoteImages` 方法签名不匹配**
- Emby 4.10 中该方法签名：`(BaseItem, LibraryOptions, RemoteImageQuery, CancellationToken)`
- 无 `IDirectoryService` 参数，Harmony Prefix/Postfix 需对应匹配

## 安装

1. 下载 `OriginalPosterPatch.dll`（GitHub Actions 构建产物）
2. 放入 Emby Server 的 `plugins` 目录
3. 重启 Emby
4. 进入 `设置` → `插件` → `Original Poster Patch`，确认已加载

## 配置

在 Emby 插件设置页面中：

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| **启用** | 开启 | 开启海报语言优先级排序 |
| **启用简繁区分** | 开启 | 简体中文海报优先于繁体中文 |

媒体库设置中，将 `首选图像下载语言` 设为期望的语言（如 `简体中文`），插件会自动按「首选语言 → 原语言 → 其他」排序。

## 排序逻辑

| 海报语言 | 优先级 |
|----------|--------|
| zh-CN / zh-SG（简体中文） | 最高 |
| zh-TW / zh-HK（繁体中文） | 高 |
| zh（通用中文） | 中高 |
| 原语言（如 ko / ja） | 中 |
| en（英文） | 低 |
| 其他 | 更低 |
| 无语言文字海报 | 最低 |

## 技术说明

- 使用 Harmony Patch 拦截 `ProviderManager.GetAvailableRemoteImages`
- 通过 Harmony Postfix 补丁捕获 TMDB `GetMovieInfo` / `EnsureSeriesInfo` 返回的 `original_language`
- 无需额外 TMDB API Key，复用 Emby 内置的 MovieDb 提供者
- 构建时 ILRepack 合并 `0Harmony.dll`，部署只需单个 DLL

## 许可证

[![License: GPL v3](https://img.shields.io/badge/License-GPL%20v3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)

本项目采用 **GPL-3.0 许可证**。
