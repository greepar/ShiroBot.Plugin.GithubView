# GithubView

解析 GitHub 仓库链接并渲染图片卡片；原有群聊触发功能保持。

## 构建与兼容性

使用 NuGet `ShiroBot.SDK 0.9.8`，最低宿主版本 0.9.8。SDK / Model 由宿主提供，无需单独引用 AvaloniaSdk。

```sh
dotnet restore ShiroBot.Plugin.GithubView.csproj
dotnet publish ShiroBot.Plugin.GithubView.csproj -c Release
```

0.9.8 尚未公开发布时，restore 使用包含本地 SDK nupkg 的 NuGet 源。部署发布目录中的插件 DLL；Avalonia 渲染需在宿主启用。
