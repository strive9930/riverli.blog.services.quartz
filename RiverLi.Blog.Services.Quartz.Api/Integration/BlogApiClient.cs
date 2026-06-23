using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RiverLi.Blog.Services.Quartz.Api.Integration;

/// <summary>
/// 专门用于调用 Blog.Api 微服务的强类型客户端
/// </summary>
public class BlogApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<BlogApiClient> _logger;

    public BlogApiClient(HttpClient httpClient, ILogger<BlogApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// 触发博客文章阅读量同步落库操作
    /// </summary>
    public async Task<bool> SyncArticleViewsAsync()
    {
        _logger.LogInformation("正在通过网关向 Blog.Api 发送同步阅读量指令...");

        // 假设您的 Blog 微服务有一个内部专用接口接收这个触发
        var response = await _httpClient.PostAsync("/api/blog/internal/articles/sync-views", null);

        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Blog.Api 响应成功！");
            return true;
        }

        var error = await response.Content.ReadAsStringAsync();
        _logger.LogError("调用 Blog.Api 失败，状态码: {StatusCode}, 错误信息: {Error}", response.StatusCode, error);
        
        // 抛出异常，这样我们的 Dapper 拦截器就能捕获到并把 IsSuccess 标为 0
        throw new HttpRequestException($"跨微服务调用失败: {response.StatusCode}");
    }
}