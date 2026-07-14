using System.Collections.Generic;

namespace RiverLi.Blog.Services.Quartz.Api.Application.Common.Dto;

/// <summary>
/// 调度器全局概览 DTO
/// </summary>
public class SchedulerInfoDto
{
    public string SchedulerName { get; set; } = "";
    public string SchedulerInstanceId { get; set; } = "";
    public string Status { get; set; } = "";          // Running / Standby / Shutdown
    public bool IsStarted { get; set; }
    public bool InStandbyMode { get; set; }
    public bool IsShutdown { get; set; }
    public int ThreadPoolSize { get; set; }
    public int JobCount { get; set; }
    public int TriggerCount { get; set; }
    public List<string> JobGroupNames { get; set; } = new();
}
