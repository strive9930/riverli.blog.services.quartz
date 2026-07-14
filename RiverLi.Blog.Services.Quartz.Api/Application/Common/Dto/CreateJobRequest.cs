namespace RiverLi.Blog.Services.Quartz.Api.Application.Common.Dto;

/// <summary>
/// 前台请求实体
/// </summary>
public class CreateJobRequest
{
    // === 基础属性 ===
    public string JobName { get; set; } = null!;
    public string JobGroup { get; set; } = "BlogJobs";
    public string Description { get; set; } = "";
    public string JobType { get; set; } = "HttpDispatchJob";

    // === 触发器 ===
    /// <summary>触发器类型: "cron" (默认) 或 "simple"</summary>
    public string TriggerType { get; set; } = "cron";

    /// <summary>Cron 表达式（TriggerType=cron 时必填）</summary>
    public string? CronExpression { get; set; }

    /// <summary>Simple Trigger: 间隔秒数（TriggerType=simple 时必填）</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>Simple Trigger: 重复次数，-1 表示无限重复，0 表示一次性</summary>
    public int RepeatCount { get; set; } = -1;

    /// <summary>Simple Trigger: 首次延迟秒数</summary>
    public int StartDelaySeconds { get; set; } = 0;

    // === HTTP 调度参数 ===
    public string RequestUrl { get; set; } = null!;
    public string HttpMethod { get; set; } = "POST";
    public string? Headers { get; set; }
    public string? Body { get; set; }
}
