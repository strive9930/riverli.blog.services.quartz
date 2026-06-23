using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Quartz;
using RiverLi.Blog.Services.Quartz.Api.Integration;

namespace RiverLi.Blog.Services.Quartz.Api.Jobs;

[DisallowConcurrentExecution]
public class ArticleViewSyncJob : IJob
{
    private readonly BlogApiClient _blogApiClient; // 🌟 注入通信客户端
    private readonly ILogger<ArticleViewSyncJob> _logger;

    public ArticleViewSyncJob(BlogApiClient blogApiClient, ILogger<ArticleViewSyncJob> logger)
    {
        _blogApiClient = blogApiClient;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("🚀 [Quartz] 开始执行跨服务任务: 文章阅读量同步");
        
        // 🌟 真正发起跨微服务 HTTP 调用
        await _blogApiClient.SyncArticleViewsAsync();
        
        _logger.LogInformation("✅ [Quartz] 跨服务任务执行完成!");
    }
}