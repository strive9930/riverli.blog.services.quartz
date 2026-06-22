using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Quartz;

namespace RiverLi.Blog.Services.Quartz.Api.Jobs;

// 阻止并发执行：保证同一个 Job 在同一时间只能跑一个实例
[DisallowConcurrentExecution]
public class ArticleViewSyncJob : IJob
{
    private readonly ILogger<ArticleViewSyncJob> _logger;

    public ArticleViewSyncJob(ILogger<ArticleViewSyncJob> logger)
    {
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("🚀 [Quartz] 开始执行文章阅读量同步任务: {Time}", DateTime.Now);
        
        // 模拟业务耗时
        await Task.Delay(2000); 
        
        _logger.LogInformation("✅ [Quartz] 文章阅读量同步完成!");
    }
}