using System.Diagnostics;

/// <summary>
/// X-Correlation-Id 归一 + JSON 结构化日志 + 请求级 scope/耗时（T10 可观测性）。
/// traceId 写入 context.TraceIdentifier，错误信封与响应头回传同一值。
/// </summary>
public static class RequestLogging
{
    /// <summary>
    /// 可选：服务侧注册请求级 ambient trace（如 PracticeService.Persistence.TraceFlow.Begin），
    /// 供出站 HTTP 客户端透传 X-Correlation-Id。未注册则不影响。
    /// </summary>
    public static Func<string, IDisposable?>? BeginAmbientTrace { get; set; }

    public static ILoggingBuilder AddJsonStructuredLogging(this ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddJsonConsole(options =>
        {
            options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
            options.UseUtcTimestamp = true;
            options.IncludeScopes = true;
            options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = false };
        });
        return logging;
    }

    /// <param name="normalizeCorrelationId">
    /// false：跳过 X-Correlation-Id 归一，沿用上游中间件已写入的 TraceIdentifier
    /// （如 GalGameService 自带的严格字符集校验）。
    /// </param>
    public static IApplicationBuilder UseRequestLogging(
        this IApplicationBuilder app, string service, bool normalizeCorrelationId = true) =>
        app.Use(async (context, next) =>
        {
            if (normalizeCorrelationId)
            {
                var incoming = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
                context.TraceIdentifier = string.IsNullOrWhiteSpace(incoming)
                    ? Guid.NewGuid().ToString("N")
                    : incoming.Length <= 128 ? incoming : Guid.NewGuid().ToString("N");
                context.Response.Headers["X-Correlation-Id"] = context.TraceIdentifier;
            }

            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(service);
            var userId = context.Request.Headers["X-User-Id"].FirstOrDefault();
            var method = context.Request.Method;
            var path = context.Request.Path.Value ?? "/";
            var start = Stopwatch.GetTimestamp();
            using (logger.BeginScope(new Dictionary<string, object?>
            {
                ["traceId"] = context.TraceIdentifier,
                ["userId"] = userId,
                ["path"] = path,
                ["method"] = method,
            }))
            using (BeginAmbientTrace?.Invoke(context.TraceIdentifier))
            {
                try
                {
                    await next();
                }
                finally
                {
                    var durationMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    logger.LogInformation(
                        "HTTP {Method} {Path} completed with {Status} in {DurationMs:F1}ms traceId={TraceId} userId={UserId}",
                        method, path, context.Response.StatusCode, durationMs, context.TraceIdentifier, userId);
                }
            }
        });
}
