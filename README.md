# GithubView

当前发布：`v1.2.0`。本版本使用 SDK `1.0.4`（.NET 11），需要宿主 `1.0.4` 及以上。

在群聊中识别 GitHub 链接并渲染图片卡片：

| 链接 | 卡片 |
| --- | --- |
| `github.com/owner/repo` | 仓库卡片：简介、贡献者、Issue、Star、Fork、语言占比；回复该卡片 `readme` 渲染完整 README |
| `github.com/owner/repo/issues/123` | Issue 卡片：标题、状态（Open / Closed / Not planned）、标签、Markdown 正文与前几条评论 |
| `github.com/owner/repo/pull/123` | PR 卡片：标题、状态（Open / Draft / Merged / Closed）、分支、Markdown 正文与前几条评论、提交 / 文件 / 增删行数 |

正文、评论与 README 按 GitHub 样式渲染 Markdown：标题、列表、任务列表、引用、提示块（`> [!NOTE]`）、代码块、表格、图片与常见 HTML（居中 logo、徽章行）。shields.io 徽章自动转为 PNG，其他 SVG 图片会被跳过。

卡片按 GitHub Primer 配色提供浅色 / 深色两套主题。

只有以上三种链接会触发；`blob`、`tree`、`releases`、`orgs`、用户主页等其他 GitHub 地址不再渲染。
指向 PR 的 `/issues/123` 链接会自动渲染为 PR 卡片，`/pull/123/files` 等 PR 子页面同样识别。

## 配置

`plugins/GithubView/config.toml`，也可在 Dashboard 中修改：

| 键 | 说明 | 默认值 |
| --- | --- | --- |
| `token` | GitHub Personal Access Token，无需任何权限。匿名访问每小时 60 次请求，填写后提升至 5000 次 | 空 |
| `theme` | 卡片配色：`auto` 跟随宿主 `avalonia_theme`，`light` / `dark` 固定 | `auto` |
| `enable_repository_card` | 渲染仓库卡片 | `true` |
| `enable_issue_card` | 渲染 Issue 卡片 | `true` |
| `enable_pull_request_card` | 渲染 PR 卡片 | `true` |
| `comment_count` | Issue / PR 卡片展示的评论条数（0–10） | `3` |
| `enable_readme_reply` | 仓库卡片显示“回复 readme”提示并响应回复 | `true` |
| `readme_reply_minutes` | 回复 readme 的有效期（分钟） | `10` |

## 构建

使用 NuGet `ShiroBot.SDK 1.0.4`，SDK / Model / Avalonia 由宿主提供；Markdown 解析使用 Markdig（打包时合并进插件 DLL）。

```sh
dotnet publish ShiroBot.Plugin.GithubView.csproj -c Release
```

部署发布目录中的插件 DLL；Avalonia 渲染需在宿主启用。
