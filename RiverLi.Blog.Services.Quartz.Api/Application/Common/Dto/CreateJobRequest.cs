namespace RiverLi.Blog.Services.Quartz.Api.Application.Common.Dto;

/// <summary>
/// 前台请求实体
/// </summary>
public class CreateJobRequest
{
    public string JobName { get; set; } = null!;
    public string JobGroup { get; set; } = "BlogJobs";
    public string Description { get; set; } = "";
    public string CronExpression { get; set; } = null!;
    public string RequestUrl { get; set; } = null!;
    // 🌟 新增的三大动态参数
    public string HttpMethod { get; set; } = "POST";
    public string? Headers { get; set; } // 要求前端传 JSON 字符串，例如 {"Authorization":"Bearer xxx"}
    public string? Body { get; set; }    // 要求前端传 JSON 字符串
}