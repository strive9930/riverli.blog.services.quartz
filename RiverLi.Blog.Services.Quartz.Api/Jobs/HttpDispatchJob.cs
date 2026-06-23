using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Quartz;

namespace RiverLi.Blog.Services.Quartz.Api.Jobs;

[DisallowConcurrentExecution]
public class HttpDispatchJob : IJob
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpDispatchJob> _logger;

    public HttpDispatchJob(IHttpClientFactory httpClientFactory, ILogger<HttpDispatchJob> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var dataMap = context.MergedJobDataMap;
        var requestUrl = dataMap.GetString("RequestUrl");
        var methodStr = dataMap.GetString("HttpMethod") ?? "POST";
        var headersStr = dataMap.GetString("Headers");
        var bodyStr = dataMap.GetString("Body");

        if (string.IsNullOrEmpty(requestUrl))
            throw new ArgumentException("该任务未配置 RequestUrl，无法执行");

        _logger.LogInformation("🚀 [HttpJob] 准备触发: {Method} {Url}", methodStr, requestUrl);

        try
        {
            // 1. 动态构造 HTTP 方法
            var method = new HttpMethod(methodStr.ToUpper());
            var request = new HttpRequestMessage(method, requestUrl);

            // 2. 动态装载 Headers (解析前端传来的 JSON 字符串)
            if (!string.IsNullOrWhiteSpace(headersStr))
            {
                try
                {
                    var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersStr);
                    if (headers != null)
                    {
                        foreach (var header in headers)
                        {
                            // 忽略验证地添加 header (有些特殊的鉴权头如果不这么写会报错)
                            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "解析请求头失败，请检查是否为合法 JSON");
                }
            }

            // 3. 动态装载 Body (仅针对 POST, PUT, PATCH 等包含内容的方法)
            if ((method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Patch) && 
                !string.IsNullOrWhiteSpace(bodyStr))
            {
                request.Content = new StringContent(bodyStr, Encoding.UTF8, "application/json");
            }

            // 4. 发起网络请求
            var client = _httpClientFactory.CreateClient("BlogApiClient");
            var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"调用失败: {response.StatusCode}, 响应: {error}");
            }
        
            _logger.LogInformation("✅ [HttpJob] 执行成功，响应码: {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [HttpJob] 执行异常: {Method} {Url}", methodStr, requestUrl);
            // 重新抛出，让 Quartz 和 ExecutionLogListener 捕获并记录
            throw new JobExecutionException(ex)
            {
                RefireImmediately = false,   // 不立即重试
                UnscheduleFiringTrigger = false,  // 不移除触发器
                UnscheduleAllTriggers = false      // 不移除所有触发器
            };
        }
    }
}