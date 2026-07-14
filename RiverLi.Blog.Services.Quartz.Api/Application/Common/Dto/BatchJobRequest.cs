using System.Collections.Generic;

namespace RiverLi.Blog.Services.Quartz.Api.Application.Common.Dto;

/// <summary>
/// 批量操作请求体
/// </summary>
public class BatchJobRequest
{
    public List<JobKeyDto> Jobs { get; set; } = new();
}

public class JobKeyDto
{
    public string JobName { get; set; } = null!;
    public string JobGroup { get; set; } = "BlogJobs";
}
