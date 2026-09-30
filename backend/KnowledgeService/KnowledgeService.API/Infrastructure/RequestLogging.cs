using System.Diagnostics;

namespace KnowledgeService.API.Infrastructure;

/// <summary>
/// JSON 结构化日志 + 请求级 scope/耗时（T10 可观测性）。
/// X-Correlation-Id 归一由既有 TraceContextMiddleware 完成，此处只消费 TraceIdentifier。
/// </summary>
public static class RequestLogging
{
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

    public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app, string service) =>
        app.Use(async (context, next) =>
        {
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
