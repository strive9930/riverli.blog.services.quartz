using Quartz;
using RiverLi.Blog.Services.Quartz.Api.Jobs;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
// 1. 添加控制器支持
builder.Services.AddControllers();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
                       ?? throw new InvalidOperationException("未找到数据库配置");

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
            sql.TablePrefix = "QRTZ_"; 
        });
        s.UseSystemTextJsonSerializer();
    });

    // 注册我们的测试任务
    var jobKey = new JobKey("SyncArticleViewsJob", "BlogJobs");
    q.AddJob<ArticleViewSyncJob>(opts => opts.WithIdentity(jobKey));
    
    // 绑定触发器：每天凌晨 00:00:00 执行
    q.AddTrigger(opts => opts
        .ForJob(jobKey)
        .WithIdentity("SyncArticleViewsTrigger", "BlogTriggers")
        .WithCronSchedule("0 0 0 * * ?") 
    );
});

// 3. 将 Quartz 注册为托管服务 (随 WebApi 启动而启动)
builder.Services.AddQuartzHostedService(options =>
{
    options.WaitForJobsToComplete = true; // 优雅停机
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.MapControllers(); // 映射我们后面要写的 JobsController
app.MapGet("/", () => "RiverLi Quartz Microservice is running!");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
