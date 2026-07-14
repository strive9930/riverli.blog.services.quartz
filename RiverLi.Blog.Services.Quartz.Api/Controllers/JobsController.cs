using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
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
    private readonly IDbConnection _dbConnection;

    public JobsController(ISchedulerFactory schedulerFactory, IDbConnection dbConnection)
    {
        _schedulerFactory = schedulerFactory;
        _dbConnection = dbConnection;
    }

    // ============================================================
    // 调度器全局概览
    // ============================================================

    /// <summary>
    /// 获取调度器全局概览信息
    /// </summary>
    [HttpGet("scheduler")]
    public async Task<IActionResult> GetSchedulerInfo()
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        var jobGroups = await scheduler.GetJobGroupNames();
        var triggerGroups = await scheduler.GetTriggerGroupNames();

        int totalJobs = 0;
        foreach (var group in jobGroups)
            totalJobs += (await scheduler.GetJobKeys(GroupMatcher<JobKey>.GroupEquals(group))).Count;

        int totalTriggers = 0;
        foreach (var group in triggerGroups)
            totalTriggers += (await scheduler.GetTriggerKeys(GroupMatcher<TriggerKey>.GroupEquals(group))).Count;

        var info = new SchedulerInfoDto
        {
            SchedulerName = scheduler.SchedulerName,
            SchedulerInstanceId = scheduler.SchedulerInstanceId,
            Status = scheduler.IsShutdown ? "Shutdown" : scheduler.InStandbyMode ? "Standby" : "Running",
            IsStarted = scheduler.IsStarted,
            InStandbyMode = scheduler.InStandbyMode,
            IsShutdown = scheduler.IsShutdown,
            ThreadPoolSize = 0, // IScheduler 不直接暴露此属性
            JobCount = totalJobs,
            TriggerCount = totalTriggers,
            JobGroupNames = jobGroups.ToList()
        };

        return Ok(new { Succeeded = true, Data = info });
    }

    /// <summary>
    /// 暂停所有任务（全局暂停）
    /// </summary>
    [HttpPost("scheduler/pause-all")]
    public async Task<IActionResult> PauseAll()
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        await scheduler.PauseAll();
        return Ok(new { Succeeded = true, Message = "已全局暂停所有任务调度" });
    }

    /// <summary>
    /// 恢复全局任务（唤醒调度器 + 恢复所有被暂停的任务）
    /// </summary>
    [HttpPost("scheduler/resume-all")]
    public async Task<IActionResult> ResumeAll()
    {
        var scheduler = await _schedulerFactory.GetScheduler();

        // 如果调度器处于待机状态，先唤醒它
        if (scheduler.InStandbyMode)
            await scheduler.Start();

        await scheduler.ResumeAll();
        return Ok(new { Succeeded = true, Message = "已恢复全局任务调度" });
    }

    /// <summary>
    /// 关闭调度器（进入待机模式，不再触发新任务，但可恢复）
    /// </summary>
    [HttpPost("scheduler/shutdown")]
    public async Task<IActionResult> Shutdown()
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        if (scheduler.IsShutdown)
            return BadRequest(new { Succeeded = false, Message = "调度器已关闭" });

        await scheduler.Standby();
        return Ok(new { Succeeded = true, Message = "调度器已关闭" });
    }

    /// <summary>
    /// 启动调度器（仅启动调度器本身，各任务保持原有状态：暂停的仍暂停，活跃的继续触发）
    /// </summary>
    [HttpPost("scheduler/start")]
    public async Task<IActionResult> Start()
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        if (scheduler.IsShutdown)
            return BadRequest(new { Succeeded = false, Message = "调度器已永久关闭，无法启动" });

        if (!scheduler.InStandbyMode)
            return Ok(new { Succeeded = true, Message = "调度器已在运行中，无需重复启动" });

        await scheduler.Start();
        return Ok(new { Succeeded = true, Message = "调度器已启动，各任务保持原有状态" });
    }

    // ============================================================
    // Job 类型列表（供前端下拉选择）
    // ============================================================

    /// <summary>
    /// 获取所有已注册的 IJob 实现类列表
    /// </summary>
    [HttpGet("job-types")]
    public IActionResult GetJobTypes()
    {
        var jobTypes = new List<object>
        {
            new { Name = nameof(HttpDispatchJob), FullName = typeof(HttpDispatchJob).FullName, Description = "通用 HTTP 调度任务 — 动态发起 HTTP 请求" }
            // 未来新增 Job 类型时在此追加
        };
        return Ok(new { Succeeded = true, Data = jobTypes });
    }

    // ============================================================
    // Job CRUD
    // ============================================================

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

        // 1. 构建 JobDetail（目前仅支持 HttpDispatchJob，未来可按 req.JobType 反射创建）
        var jobDetail = JobBuilder.Create<HttpDispatchJob>()
            .WithIdentity(jobKey)
            .WithDescription(req.Description)
            .UsingJobData("RequestUrl", req.RequestUrl)
            .UsingJobData("HttpMethod", req.HttpMethod)
            .UsingJobData("Headers", req.Headers ?? "")
            .UsingJobData("Body", req.Body ?? "")
            .Build();

        // 2. 构建触发器：支持 Cron 和 Simple 两种模式
        ITrigger trigger;
        var triggerIdentity = new TriggerKey($"{req.JobName}_Trigger", req.JobGroup);

        if (req.TriggerType == "simple")
        {
            // Simple Trigger: 按间隔执行
            var simpleBuilder = TriggerBuilder.Create()
                .WithIdentity(triggerIdentity)
                .StartAt(DateTimeOffset.Now.AddSeconds(req.StartDelaySeconds));

            if (req.RepeatCount < 0)
            {
                // 无限重复
                simpleBuilder = simpleBuilder.WithSimpleSchedule(x => x
                    .WithIntervalInSeconds(req.IntervalSeconds)
                    .RepeatForever()
                    .WithMisfireHandlingInstructionNextWithRemainingCount());
            }
            else if (req.RepeatCount > 0)
            {
                simpleBuilder = simpleBuilder.WithSimpleSchedule(x => x
                    .WithIntervalInSeconds(req.IntervalSeconds)
                    .WithRepeatCount(req.RepeatCount)
                    .WithMisfireHandlingInstructionNextWithRemainingCount());
            }
            else
            {
                // 一次性
                simpleBuilder = simpleBuilder.WithSimpleSchedule(x => x
                    .WithRepeatCount(0));
            }

            trigger = simpleBuilder.Build();
        }
        else
        {
            // Cron Trigger（默认）
            if (string.IsNullOrWhiteSpace(req.CronExpression))
                return BadRequest(new { Succeeded = false, Message = "Cron 表达式不能为空" });
            if (!CronExpression.IsValidExpression(req.CronExpression))
                return BadRequest(new { Succeeded = false, Message = "Cron 表达式格式错误" });

            trigger = TriggerBuilder.Create()
                .WithIdentity(triggerIdentity)
                .WithCronSchedule(req.CronExpression)
                .Build();
        }

        // 3. 落库排期
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
    /// 更新调度任务
    /// </summary>
    [HttpPut("{jobName}")]
    public async Task<IActionResult> UpdateJob(string jobName, [FromQuery] string group, [FromBody] UpdateJobRequest req)
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        var jobKey = new JobKey(jobName, group);

        if (!await scheduler.CheckExists(jobKey))
            return NotFound(new { Succeeded = false, Message = "找不到指定的任务" });

        var existingJob = await scheduler.GetJobDetail(jobKey);
        if (existingJob == null)
            return NotFound(new { Succeeded = false, Message = "任务详情获取失败" });

        var dataMap = new JobDataMap((IDictionary<string, object>)existingJob.JobDataMap);
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

        // 2. 重建 Trigger（支持 Cron 和 Simple 两种模式）
        var triggers = await scheduler.GetTriggersOfJob(jobKey);
        var existingTrigger = triggers.OrderByDescending(t => t is ICronTrigger)
            .ThenBy(t => t.Key.Group == "DEFAULT" ? 1 : 0)
            .FirstOrDefault();
        ITrigger newTrigger;
        var triggerKey = existingTrigger?.Key ?? new TriggerKey($"{jobName}_Trigger", group);

        // 判断触发器类型：优先用请求指定的，否则根据原有类型推断
        var triggerType = req.TriggerType
            ?? (existingTrigger is ISimpleTrigger ? "simple" : "cron");

        if (triggerType == "simple")
        {
            var intervalSec = req.IntervalSeconds ?? (existingTrigger is ISimpleTrigger st ? (int)st.RepeatInterval.TotalSeconds : 60);
            var repeatCnt = req.RepeatCount ?? (existingTrigger is ISimpleTrigger st2 ? st2.RepeatCount : -1);

            var simpleBuilder = TriggerBuilder.Create()
                .WithIdentity(triggerKey)
                .ForJob(jobDetail);

            if (repeatCnt < 0)
                simpleBuilder = simpleBuilder.WithSimpleSchedule(x => x.WithIntervalInSeconds(intervalSec).RepeatForever());
            else if (repeatCnt > 0)
                simpleBuilder = simpleBuilder.WithSimpleSchedule(x => x.WithIntervalInSeconds(intervalSec).WithRepeatCount(repeatCnt));
            else
                simpleBuilder = simpleBuilder.WithSimpleSchedule(x => x.WithRepeatCount(0));

            newTrigger = simpleBuilder.Build();
        }
        else
        {
            if (req.CronExpression is not null)
            {
                if (!CronExpression.IsValidExpression(req.CronExpression))
                    return BadRequest(new { Succeeded = false, Message = "Cron 表达式格式错误" });

                newTrigger = TriggerBuilder.Create()
                    .WithIdentity(triggerKey)
                    .ForJob(jobDetail)
                    .WithCronSchedule(req.CronExpression)
                    .Build();
            }
            else if (existingTrigger is ICronTrigger cronTrigger && cronTrigger.CronExpressionString is not null)
            {
                newTrigger = TriggerBuilder.Create()
                    .WithIdentity(triggerKey)
                    .ForJob(jobDetail)
                    .WithCronSchedule(cronTrigger.CronExpressionString)
                    .Build();
            }
            else
            {
                newTrigger = TriggerBuilder.Create()
                    .WithIdentity(triggerKey)
                    .ForJob(jobDetail)
                    .StartNow()
                    .Build();
            }
        }

        await scheduler.DeleteJob(jobKey);
        await scheduler.ScheduleJob(jobDetail, newTrigger);
        return Ok(new { Succeeded = true, Message = $"任务 [{jobName}] 更新成功" });
    }

    // ============================================================
    // Job 列表查询（支持筛选）
    // ============================================================

    /// <summary>
    /// 获取所有定时任务（支持按 Group / State 筛选）
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAllJobs(
        [FromQuery] string? group = null,
        [FromQuery] string? state = null)
    {
        var scheduler = await _schedulerFactory.GetScheduler();

        IEnumerable<JobKey> jobKeys;
        if (!string.IsNullOrWhiteSpace(group))
            jobKeys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.GroupEquals(group));
        else
            jobKeys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());

        var jobInfos = new List<object>();

        foreach (var jobKey in jobKeys)
        {
            var jobDetail = await scheduler.GetJobDetail(jobKey);
            if (jobDetail == null) continue;

            var triggers = await scheduler.GetTriggersOfJob(jobKey);
            var primaryTrigger = triggers
                .OrderByDescending(t => t is ICronTrigger)
                .ThenBy(t => t.Key.Group == "DEFAULT" ? 1 : 0)
                .FirstOrDefault();

            if (primaryTrigger == null) continue;

            var triggerState = await scheduler.GetTriggerState(primaryTrigger.Key);
            var stateStr = triggerState.ToString();

            // 状态筛选
            if (!string.IsNullOrWhiteSpace(state) && !stateStr.Equals(state, StringComparison.OrdinalIgnoreCase))
                continue;

            var cronTrigger = primaryTrigger as ICronTrigger;
            var simpleTrigger = primaryTrigger as ISimpleTrigger;

            jobInfos.Add(new
            {
                JobName = jobKey.Name,
                JobGroup = jobKey.Group,
                Description = jobDetail.Description ?? "暂无描述",
                JobType = jobDetail.JobType?.Name ?? "Unknown",
                TriggerName = primaryTrigger.Key.Name,
                TriggerGroup = primaryTrigger.Key.Group,
                TriggerType = cronTrigger != null ? "Cron" : "Simple",
                State = stateStr,
                CronExpression = cronTrigger?.CronExpressionString,
                RepeatInterval = simpleTrigger != null ? (long?)simpleTrigger.RepeatInterval.TotalSeconds : null,
                RepeatCount = simpleTrigger?.RepeatCount,
                TimesTriggered = simpleTrigger?.TimesTriggered,
                NextFireTime = primaryTrigger.GetNextFireTimeUtc()?.LocalDateTime,
                PreviousFireTime = primaryTrigger.GetPreviousFireTimeUtc()?.LocalDateTime
            });
        }

        return Ok(new
        {
            Succeeded = true,
            Data = jobInfos,
            TotalCount = jobInfos.Count
        });
    }

    // ============================================================
    // 单个 Job 操作
    // ============================================================

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

    /// <summary>
    /// 获取单个 Job 的 JobDataMap（键值对参数）
    /// </summary>
    [HttpGet("{jobName}/data")]
    public async Task<IActionResult> GetJobData(string jobName, [FromQuery] string group = "BlogJobs")
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        var jobKey = new JobKey(jobName, group);
        var jobDetail = await scheduler.GetJobDetail(jobKey);
        if (jobDetail == null)
            return NotFound(new { Succeeded = false, Message = "找不到指定的任务" });

        var dict = new Dictionary<string, string>();
        foreach (var key in jobDetail.JobDataMap.Keys)
            dict[key] = jobDetail.JobDataMap.GetString(key) ?? "";

        return Ok(new { Succeeded = true, Data = dict });
    }

    // ============================================================
    // 批量操作
    // ============================================================

    /// <summary>
    /// 批量暂停任务
    /// </summary>
    [HttpPost("batch/pause")]
    public async Task<IActionResult> BatchPause([FromBody] BatchJobRequest req)
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        var successCount = 0;
        var errors = new List<string>();

        foreach (var job in req.Jobs)
        {
            var jobKey = new JobKey(job.JobName, job.JobGroup);
            if (await scheduler.CheckExists(jobKey))
            {
                await scheduler.PauseJob(jobKey);
                successCount++;
            }
            else
            {
                errors.Add($"{job.JobName}@{job.JobGroup}: 不存在");
            }
        }

        return Ok(new
        {
            Succeeded = true,
            Message = $"批量暂停完成: 成功 {successCount} 个",
            Errors = errors
        });
    }

    /// <summary>
    /// 批量恢复任务
    /// </summary>
    [HttpPost("batch/resume")]
    public async Task<IActionResult> BatchResume([FromBody] BatchJobRequest req)
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        var successCount = 0;
        var errors = new List<string>();

        foreach (var job in req.Jobs)
        {
            var jobKey = new JobKey(job.JobName, job.JobGroup);
            if (await scheduler.CheckExists(jobKey))
            {
                await scheduler.ResumeJob(jobKey);
                successCount++;
            }
            else
            {
                errors.Add($"{job.JobName}@{job.JobGroup}: 不存在");
            }
        }

        return Ok(new
        {
            Succeeded = true,
            Message = $"批量恢复完成: 成功 {successCount} 个",
            Errors = errors
        });
    }

    /// <summary>
    /// 批量删除任务
    /// </summary>
    [HttpPost("batch/delete")]
    public async Task<IActionResult> BatchDelete([FromBody] BatchJobRequest req)
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        var successCount = 0;
        var errors = new List<string>();

        foreach (var job in req.Jobs)
        {
            var jobKey = new JobKey(job.JobName, job.JobGroup);
            if (await scheduler.CheckExists(jobKey))
            {
                await scheduler.DeleteJob(jobKey);
                successCount++;
            }
            else
            {
                errors.Add($"{job.JobName}@{job.JobGroup}: 不存在");
            }
        }

        return Ok(new
        {
            Succeeded = true,
            Message = $"批量删除完成: 成功 {successCount} 个",
            Errors = errors
        });
    }

    // ============================================================
    // 分组操作
    // ============================================================

    /// <summary>
    /// 暂停整个分组
    /// </summary>
    [HttpPost("group/{group}/pause")]
    public async Task<IActionResult> PauseGroup(string group)
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        await scheduler.PauseJobs(GroupMatcher<JobKey>.GroupEquals(group));
        return Ok(new { Succeeded = true, Message = $"已暂停分组 [{group}] 下所有任务" });
    }

    /// <summary>
    /// 恢复整个分组
    /// </summary>
    [HttpPost("group/{group}/resume")]
    public async Task<IActionResult> ResumeGroup(string group)
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        await scheduler.ResumeJobs(GroupMatcher<JobKey>.GroupEquals(group));
        return Ok(new { Succeeded = true, Message = $"已恢复分组 [{group}] 下所有任务" });
    }

    // ============================================================
    // 执行日志
    // ============================================================

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
            SELECT FireTime, RunTimeMs, IsSuccess, ErrorMessage
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
}
