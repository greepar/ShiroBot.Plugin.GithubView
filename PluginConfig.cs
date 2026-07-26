using ShiroBot.SDK.Config;

namespace ShiroBot.Plugin.GithubView;

public sealed class PluginConfig
{
    [ConfigField("GitHub Personal Access Token。留空使用匿名请求(60 次/小时),配置后提升到 5000 次/小时,订阅轮询建议配置。",
        Label = "GitHub Token", Placeholder = "ghp_xxxx")]
    public string GithubToken { get; set; } = "";

    [ConfigField("订阅轮询间隔(分钟)。", Label = "轮询间隔", Type = "number", Min = 1, Max = 120)]
    public int PollIntervalMinutes { get; set; } = 5;

    [ConfigField("README / Issue / Release 正文渲染的最大字符数,超出截断。",
        Label = "正文最大长度", Type = "number", Min = 500, Max = 20000)]
    public int MaxBodyLength { get; set; } = 4000;

    [ConfigField("列表卡片显示的条目数。", Label = "列表条目数", Type = "number", Min = 3, Max = 15)]
    public int ListItemCount { get; set; } = 8;
}
