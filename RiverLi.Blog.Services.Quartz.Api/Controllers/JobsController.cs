using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Quartz;
using Quartz.Impl.Matchers;
using RiverLi.Blog.Services.Quartz.Api.Application.Common.Dto;
using RiverLi.Blog.Services.Quartz.Api.Jobs;

namespace RiverLi.Blog.Services.Quartz.Api.Controllers;

/// <summary>
/// 调度中心后台管理 API
/// 注意：生产环境请务必添加 [Authorize] 保护这些接口！
/// </summary>
[ApiController]
[Route("api/quartz/[controller]")]
public class JobsController : ControllerBase
{
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly IDbConnection _dbConnection; // 🌟 直接注入 RiverLi.Infrastructure.Dapper 提供的连接

    public JobsController(ISchedulerFactory schedulerFactory, IDbConnection dbConnection)
    {
        _schedulerFactory = schedulerFactory;
        _dbConnection = dbConnection;
    }
    
    /// <summary>
    /// 动态新建调度任务
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateJob([FromBody] CreateJobRequest req)
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        var jobKey = new JobKey(req.JobName, req.JobGroup);

        if (await scheduler.CheckExists(jobKey))
            return BadRequest(new { Succeeded = false, Message = "该任务名称已存在" });

        // 校验 Cron 表达式合法性
        if (!CronExpression.IsValidExpression(req.CronExpression))
            return BadRequest(new { Succeeded = false, Message = "Cron 表达式格式错误" });

        // 1. 绑定到我们刚刚写的通用 HttpDispatchJob
        var jobDetail = JobBuilder.Create<HttpDispatchJob>()
            .WithIdentity(jobKey)
            .WithDescription(req.Description)
            // 🌟 核心：把前台填写的 URL 塞进任务参数里
            .UsingJobData("RequestUrl", req.RequestUrl) 
            .UsingJobData("HttpMethod", req.HttpMethod)
            .UsingJobData("Headers", req.Headers ?? "")
            .UsingJobData("Body", req.Body ?? "")
            .Build();

        // 2. 创建触发器
        var trigger = TriggerBuilder.Create()
            .WithIdentity($"{req.JobName}_Trigger", req.JobGroup)
            .WithCronSchedule(req.CronExpression)
            .Build();

        // 3. 落库排期！
        await scheduler.ScheduleJob(jobDetail, trigger);

        return Ok(new { Succeeded = true, Message = "任务创建成功" });
    }
    
    /// <summary>
    /// 删除调度任务
    /// </summary>
    [HttpDelete("{jobName}")]
    public async Task<IActionResult> DeleteJob(string jobName, [FromQuery] string group = "BlogJobs")
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        var jobKey = new JobKey(jobName, group);

        if (!await scheduler.CheckExists(jobKey))
            return NotFound(new { Succeeded = false, Message = "找不到指定的任务" });

        await scheduler.DeleteJob(jobKey);
        return Ok(new { Succeeded = true, Message = $"已删除任务: [{jobName}]" });
    }

    /// <summary>
    /// 更新调度任务（Cron / URL / 请求参数 / 描述）
    /// 仅更新传入的字段，未传的保持不变
    /// </summary>
    [HttpPut("{jobName}")]
    public async Task<IActionResult> UpdateJob(string jobName, [FromQuery] string group, [FromBody] UpdateJobRequest req)
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        var jobKey = new JobKey(jobName, group);

        if (!await scheduler.CheckExists(jobKey))
            return NotFound(new { Succeeded = false, Message = "找不到指定的任务" });

        // 1. 获取原有 JobDetail 并重建 JobDataMap
        var existingJob = await scheduler.GetJobDetail(jobKey);
        if (existingJob == null)
            return NotFound(new { Succeeded = false, Message = "任务详情获取失败" });

        var dataMap = new JobDataMap((IDictionary<string, object>)existingJob.JobDataMap);

        // 仅覆盖传入的字段
        if (req.RequestUrl is not null)  dataMap["RequestUrl"] = req.RequestUrl;
        if (req.HttpMethod is not null)  dataMap["HttpMethod"] = req.HttpMethod;
        if (req.Headers is not null)     dataMap["Headers"] = req.Headers;
        if (req.Body is not null)        dataMap["Body"] = req.Body;

        var jobDetail = JobBuilder.Create<HttpDispatchJob>()
            .WithIdentity(jobKey)
            .WithDescription(req.Description ?? existingJob.Description)
            .SetJobData(dataMap)
            .StoreDurably(existingJob.Durable)
            .Build();

        // 2. 重建 Trigger（仅当传了新的 CronExpression 时）
        var triggers = await scheduler.GetTriggersOfJob(jobKey);
        var existingTrigger = triggers.OrderByDescending(t => t is ICronTrigger)
            .ThenBy(t => t.Key.Group == "DEFAULT" ? 1 : 0)
            .FirstOrDefault();
        ITrigger newTrigger;

        if (req.CronExpression is not null)
        {
            if (!CronExpression.IsValidExpression(req.CronExpression))
                return BadRequest(new { Succeeded = false, Message = "Cron 表达式格式错误" });

            var triggerKey = existingTrigger?.Key ?? new TriggerKey($"{jobName}_Trigger", group);
            newTrigger = TriggerBuilder.Create()
                .WithIdentity(triggerKey)
                .ForJob(jobDetail)
                .WithCronSchedule(req.CronExpression)
                .Build();
        }
        else
        {
            // 保持原有触发器，但绑定新 JobDetail
            if (existingTrigger is ICronTrigger cronTrigger && cronTrigger.CronExpressionString is not null)
            {
                newTrigger = TriggerBuilder.Create()
                    .WithIdentity(existingTrigger.Key)
                    .ForJob(jobDetail)
                    .WithCronSchedule(cronTrigger.CronExpressionString)
                    .Build();
            }
            else
            {
                newTrigger = TriggerBuilder.Create()
                    .WithIdentity(existingTrigger?.Key ?? new TriggerKey($"{jobName}_Trigger", group))
                    .ForJob(jobDetail)
                    .StartNow()
                    .Build();
            }
        }

        // 3. 替换任务（删旧建新）
        await scheduler.DeleteJob(jobKey);
        await scheduler.ScheduleJob(jobDetail, newTrigger);

        return Ok(new { Succeeded = true, Message = $"任务 [{jobName}] 更新成功" });
    }

    /// <summary>
    /// 获取任务执行日志（分页）
    /// </summary>
    [HttpGet("{jobName}/logs")]
    public async Task<IActionResult> GetJobLogs(
        string jobName,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var offset = (page - 1) * pageSize;

        var sql = @"
            SELECT 
                FireTime, 
                RunTimeMs, 
                IsSuccess, 
                ErrorMessage 
            FROM QRTZ_EXECUTION_LOGS 
            WHERE JobName = @JobName 
            ORDER BY FireTime DESC 
            LIMIT @PageSize OFFSET @Offset";

        var countSql = @"SELECT COUNT(*) FROM QRTZ_EXECUTION_LOGS WHERE JobName = @JobName";

        var logs = await _dbConnection.QueryAsync(sql, new { JobName = jobName, PageSize = pageSize, Offset = offset });
        var totalCount = await _dbConnection.ExecuteScalarAsync<int>(countSql, new { JobName = jobName });

        return Ok(new
        {
            Succeeded = true,
            Data = logs,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
        });
    }
    /// <summary>
    /// 获取大盘所有的定时任务与触发器状态
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAllJobs()
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        // 获取所有分组下的 JobKey
        var jobKeys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
        
        var jobInfos = new List<object>();

        foreach (var jobKey in jobKeys)
        {
            var jobDetail = await scheduler.GetJobDetail(jobKey);
            if (jobDetail == null) continue;

            // 获取该 Job 绑定的所有 Trigger
            var triggers = await scheduler.GetTriggersOfJob(jobKey);

            // 取主触发器：优先 CronTrigger，跳过 TriggerJob 自动生成的一次性 SimpleTrigger
            var primaryTrigger = triggers
                .OrderByDescending(t => t is ICronTrigger)
                .ThenBy(t => t.Key.Group == "DEFAULT" ? 1 : 0)
                .FirstOrDefault();

            if (primaryTrigger == null) continue;

            var triggerState = await scheduler.GetTriggerState(primaryTrigger.Key);
            var cronTrigger = primaryTrigger as ICronTrigger;

            jobInfos.Add(new
            {
                JobName = jobKey.Name,
                JobGroup = jobKey.Group,
                Description = jobDetail.Description ?? "暂无描述",
                TriggerName = primaryTrigger.Key.Name,
                TriggerGroup = primaryTrigger.Key.Group,
                State = triggerState.ToString(),
                CronExpression = cronTrigger?.CronExpressionString ?? "N/A",
                NextFireTime = primaryTrigger.GetNextFireTimeUtc()?.LocalDateTime,
                PreviousFireTime = primaryTrigger.GetPreviousFireTimeUtc()?.LocalDateTime
            });
        }

        // 包装成前端标准响应格式
        return Ok(new
        {
            Succeeded = true,
            Data = jobInfos,
            TotalCount = jobInfos.Count
        });
    }

    /// <summary>
    /// 立即触发执行一次
    /// (无视 Cron 时间，强行在后台新建一个线程跑一次)
    /// </summary>
    [HttpPost("{jobName}/trigger")]
    public async Task<IActionResult> TriggerJob(string jobName, [FromQuery] string group = "BlogJobs")
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        var jobKey = new JobKey(jobName, group);

        if (!await scheduler.CheckExists(jobKey))
            return NotFound(new { Succeeded = false, Message = "找不到指定的任务" });

        await scheduler.TriggerJob(jobKey);
        return Ok(new { Succeeded = true, Message = $"指令已下发: 立即执行 [{jobName}]" });
    }

    /// <summary>
    /// 暂停调度任务
    /// </summary>
    [HttpPost("{jobName}/pause")]
    public async Task<IActionResult> PauseJob(string jobName, [FromQuery] string group = "BlogJobs")
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        var jobKey = new JobKey(jobName, group);
        
        if (!await scheduler.CheckExists(jobKey))
            return NotFound(new { Succeeded = false, Message = "找不到指定的任务" });

        await scheduler.PauseJob(jobKey);
        return Ok(new { Succeeded = true, Message = $"已挂起调度: [{jobName}]" });
    }

    /// <summary>
    /// 恢复调度任务
    /// </summary>
    [HttpPost("{jobName}/resume")]
    public async Task<IActionResult> ResumeJob(string jobName, [FromQuery] string group = "BlogJobs")
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        var jobKey = new JobKey(jobName, group);
        
        if (!await scheduler.CheckExists(jobKey))
            return NotFound(new { Succeeded = false, Message = "找不到指定的任务" });

        await scheduler.ResumeJob(jobKey);
        return Ok(new { Succeeded = true, Message = $"已恢复调度: [{jobName}]" });
    }
}