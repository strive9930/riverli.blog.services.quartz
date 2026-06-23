using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace RiverLi.Blog.Services.Quartz.Api.Listeners;

public class ExecutionLogListener : IJobListener
{
    // 🌟 核心：注入 IServiceScopeFactory 而不是直接注入 IDbConnection
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExecutionLogListener> _logger;

    public string Name => "GlobalExecutionLogListener";

    public ExecutionLogListener(IServiceScopeFactory scopeFactory, ILogger<ExecutionLogListener> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public async Task JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default)
    {
        try
        {
            var isSuccess = jobException == null;
            var runTimeMs = (long)context.JobRunTime.TotalMilliseconds;
            var errorMessage = jobException?.InnerException?.Message ?? jobException?.Message;

            var logSql = @"
                INSERT INTO QRTZ_EXECUTION_LOGS 
                (Id, JobName, JobGroup, FireTime, RunTimeMs, IsSuccess, ErrorMessage) 
                VALUES 
                (@Id, @JobName, @JobGroup, @FireTime, @RunTimeMs, @IsSuccess, @ErrorMessage)";

            // 🌟 开启一个新的 Scoped 作用域
            using var scope = _scopeFactory.CreateScope();
            
            // 🌟 从您的 RiverLi.Infrastructure.Dapper 容器中安全解析出 IDbConnection
            var connection = scope.ServiceProvider.GetRequiredService<IDbConnection>();

            // 直接使用 Dapper 执行，复用基础设施的连接管控
            await connection.ExecuteAsync(logSql, new
            {
                Id = Guid.NewGuid().ToString(),
                JobName = context.JobDetail.Key.Name,
                JobGroup = context.JobDetail.Key.Group,
                FireTime = context.FireTimeUtc.LocalDateTime,
                RunTimeMs = runTimeMs,
                IsSuccess = isSuccess,
                ErrorMessage = errorMessage
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存 Quartz 任务执行日志失败");
        }
    }
}