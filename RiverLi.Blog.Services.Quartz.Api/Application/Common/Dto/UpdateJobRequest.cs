namespace RiverLi.Blog.Services.Quartz.Api.Application.Common.Dto;

/// <summary>
/// 更新任务请求实体（所有字段可选，仅更新传入的字段）
/// </summary>
public class UpdateJobRequest
{
    /// <summary>新的 Cron 表达式（留空则不更新）</summary>
    public string? CronExpression { get; set; }

    /// <summary>新的请求 URL</summary>
    public string? RequestUrl { get; set; }

    /// <summary>新的 HTTP 方法</summary>
    public string? HttpMethod { get; set; }

    /// <summary>新的请求头 JSON</summary>
    public string? Headers { get; set; }

    /// <summary>新的请求体 JSON</summary>
    public string? Body { get; set; }

    /// <summary>新的任务描述</summary>
    public string? Description { get; set; }
}
