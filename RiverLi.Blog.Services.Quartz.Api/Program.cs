using Microsoft.Extensions.Http.Resilience;
using Polly;
using Quartz;
using RiverLi.Blog.Infrastructure.Shared.Consul;
using RiverLi.Blog.Infrastructure.Shared.Extensions;
using RiverLi.Blog.Services.Quartz.Api.Integration;
using RiverLi.Blog.Services.Quartz.Api.Jobs;
using RiverLi.Blog.Services.Quartz.Api.Listeners;

var builder = WebApplication.CreateBuilder(args);

// ==========================================
// 0. 从 Consul 配置中心拉取远程配置（覆盖本地 appsettings）
// ==========================================
builder.Configuration.AddConsulConfiguration(builder.Configuration.GetSection("Consul"));

// ==========================================
// 1. 【共享基建注入】
// ==========================================

builder.Services.AddControllers();

builder.Services.AddLoggingSupport(builder.Configuration, "QuartzService");
builder.Services.AddRiverTracing("RiverLi.QuartzService");

builder.Services.AddInfrastructureSharedServices(options =>
{
    options.EnableGlobalExceptionHandler = true;
    options.EnableCors = true;
    options.AllowedOrigins = new[] { "http://localhost:5000", "http://localhost:3000", "http://localhost:5173" };
    options.EnableOpenApiDocumentation = true;
    options.ScalarTitle = "RiverLi Blog Quartz API";
    options.ScalarVersion = "v1";
    options.ScalarDescription = "Quartz任务调度微服务接口文档";
});

builder.Services.AddHealthCheckSupport(builder.Configuration);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
                       ?? throw new InvalidOperationException("未找到数据库配置");
// 🌟 1. 注册 RiverLi 专属的 Dapper 基础设施
builder.Services.AddRiverLiDapperMySql(connectionString);
// 2. 核心：配置 Quartz 引擎
builder.Services.AddQuartz(q =>
{
    q.SchedulerId = "RiverLi-Blog-Scheduler";
    q.SchedulerName = "Blog Scheduler";

    // 启用 MySQL 持久化
    q.UsePersistentStore(s =>
    {
        s.UseProperties = true;
        s.RetryInterval = TimeSpan.FromSeconds(15);
        s.UseMySqlConnector(sql =>
        {
            sql.ConnectionString = connectionString;
            sql.TablePrefix = "qrtz_"; 
        });
        s.UseSystemTextJsonSerializer();
    });
    
    // 🌟 将我们的日志监听器注册为单例并挂载到全局
    builder.Services.AddSingleton<ExecutionLogListener>();
    q.AddJobListener<ExecutionLogListener>();

    // 注册文章定时发布扫描 Job（每分钟执行一次）
    var publishJobKey = new JobKey("PublishScheduledArticles", "SystemJobs");
    q.AddJob<HttpDispatchJob>(opts => opts
        .WithIdentity(publishJobKey)
        .WithDescription("每分钟扫描到期的定时文章并发布")
        .UsingJobData("RequestUrl", "/api/blog/internal/article/publish-scheduled")
        .UsingJobData("HttpMethod", "POST")
        .UsingJobData("Headers", "")
        .UsingJobData("Body", "")
        .StoreDurably()
    );
    q.AddTrigger(opts => opts
        .ForJob(publishJobKey)
        .WithIdentity("PublishScheduledArticles_Trigger", "SystemJobs")
        .WithCronSchedule("0 * * * * ?") // 每分钟第 0 秒执行
    );

    #region 测试任务

    // // 注册我们的测试任务
    // var jobKey = new JobKey("SyncArticleViewsJob", "BlogJobs");
    // q.AddJob<ArticleViewSyncJob>(opts => opts.WithIdentity(jobKey));
    //
    // // 绑定触发器：每天凌晨 00:00:00 执行
    // q.AddTrigger(opts => opts
    //     .ForJob(jobKey)
    //     .WithIdentity("SyncArticleViewsTrigger", "BlogTriggers")
    //     .WithCronSchedule("0 0 0 * * ?") 
    // );

    #endregion
    
});

// 3. 将 Quartz 注册为托管服务 (随 WebApi 启动而启动)
builder.Services.AddQuartzHostedService(options =>
{
    options.WaitForJobsToComplete = true; // 优雅停机
});


// 命名客户端 "BlogApiClient" —— 供 HttpDispatchJob 动态调度使用
builder.Services.AddHttpClient("BlogApiClient", client =>
{
    client.BaseAddress = new Uri("http://localhost:5000"); 
    client.DefaultRequestHeaders.Add("X-Internal-Secret", "RiverLi_Super_Secret_2026");
});

// 类型化客户端 BlogApiClient —— 供业务代码强类型注入使用
builder.Services.AddHttpClient<BlogApiClient>(client =>
{
    client.BaseAddress = new Uri("http://localhost:5000"); 
    client.DefaultRequestHeaders.Add("X-Internal-Secret", "RiverLi_Super_Secret_2026");
})
// 🌟 为这个 HttpClient 挂载量身定制的 Polly 弹性管道
.AddResilienceHandler("blog-api-pipeline", resilienceBuilder =>
{
    // 1. 超时策略 (Timeout) - 防卡死
    // 如果请求超过 10 秒还没响应，直接抛出 TimeoutRejectedException，不让线程一直傻等
    resilienceBuilder.AddTimeout(TimeSpan.FromSeconds(10));

    // 2. 重试策略 (Retry) - 防网络抖动
    resilienceBuilder.AddRetry(new HttpRetryStrategyOptions
    {
        MaxRetryAttempts = 3, // 最多重试 3 次
        Delay = TimeSpan.FromSeconds(2), // 初始等待时间 2 秒
        BackoffType = DelayBackoffType.Exponential, // 指数退避：等待时间会变成 2s, 4s, 8s...
        // 默认会自动捕获 5xx 错误、408 请求超时以及网络异常进行重试
    });

    // 3. 熔断器策略 (Circuit Breaker) - 防雪崩
    resilienceBuilder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
    {
        FailureRatio = 0.5, // 错误率阈值：如果 50% 的请求都失败了
        SamplingDuration = TimeSpan.FromSeconds(30), // 在 30 秒的统计窗口内
        MinimumThroughput = 5, // 且至少有 5 个请求打过来
        BreakDuration = TimeSpan.FromSeconds(30) // 触发熔断！接下来 30 秒内所有请求直接拦截报错，给下游 Blog.Api 喘息恢复的时间
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseInfrastructureSharedMiddlewares();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();// 映射我们后面要写的 JobsController

app.MapInfrastructureSharedEndpoints(options => {
    options.EnableOpenApiDocumentation = app.Environment.IsDevelopment();
    options.ScalarTitle = "RiverLi Blog Quartz API";
});

app.Run();
