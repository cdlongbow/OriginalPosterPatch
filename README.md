# Original Poster Patch

> **Emby Plugin** — Harmony Patch 方式实现海报语言优先级排序，无需独立 TMDB API Key。

## 功能

| 功能 | 说明 |
|------|------|
| 首选语言优先 | 优先显示媒体库设置的「首选图像下载语言」的海报 |
| 原语言回退 | 首选语言无图时，自动回退到 TMDB 原语言海报 |
| 简繁区分 | 简体中文（zh-CN）优先于繁体中文（zh-TW/zh-HK） |
| 零配置 | 无需 TMDB API Key，复用 Emby 内置 MovieDb 提供者 |
| 全类型支持 | 电影 / 剧集 / 播出季 / 单集 / 合集 |

## 安装

1. 下载 `Merged-OriginalPosterPatch.dll`（GitHub Actions 构建产物）
2. 放入 Emby Server 的 `plugins` 目录
3. 重启 Emby
4. 进入 `设置` → `插件` → `Original Poster Patch`，确认已加载

## 配置

在 Emby 插件设置页面中：

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| **启用** | 开启 | 开启海报语言优先级排序 |
| **启用简繁区分** | 开启 | 简体中文海报优先于繁体中文 |

媒体库设置中，将 `首选图像下载语言` 设为期望的语言（如 `zh`），插件会自动按「首选语言 → 原语言 → 其他」排序。

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