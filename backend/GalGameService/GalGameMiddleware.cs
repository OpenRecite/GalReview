using Microsoft.AspNetCore.Diagnostics;

public static class GalGameMiddleware
{
    // X-Correlation-Id：校验格式并截断长度，防止头注入和日志注入
    public static void UseGalGameCorrelationId(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            const int MaxCorrelationIdLength = 64;
            var rawCorrelationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();

            string correlationId;
            if (!string.IsNullOrWhiteSpace(rawCorrelationId)
                && rawCorrelationId.Length <= MaxCorrelationIdLength
                && rawCorrelationId.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'))
            {
                correlationId = rawCorrelationId;
            }
            else
            {
                correlationId = Guid.NewGuid().ToString("N");
            }

            context.TraceIdentifier = correlationId;
            context.Response.Headers["X-Correlation-Id"] = correlationId;
            await next();
        });
    }

    // 异常处理
    public static void UseGalGameExceptionHandler(this WebApplication app)
    {
        app.UseExceptionHandler(error => error.Run(context =>
        {
            var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GalGameService");

            if (exception is UpstreamContractException)
            {
                logger.LogError(exception, "KnowledgeService returned an invalid contract. CorrelationId: {CorrelationId}; Path: {Path}",
                    context.TraceIdentifier, context.Request.Path);
                return Results.Json(
                    ApiFailure.Create("UPSTREAM_CONTRACT_INVALID", "知识图谱服务响应不符合契约", context.TraceIdentifier),
                    statusCode: StatusCodes.Status502BadGateway).ExecuteAsync(context);
            }
            if (exception is BadHttpRequestException or System.Text.Json.JsonException)
            {
                logger.LogWarning(exception, "Invalid GalGameService request. CorrelationId: {CorrelationId}; Path: {Path}",
                    context.TraceIdentifier, context.Request.Path);
                return Results.Json(
                    ApiFailure.Create("VALIDATION_ERROR", "请求 JSON、参数或字段格式错误", context.TraceIdentifier),
                    statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context);
            }
            logger.LogError(exception, "Unhandled GalGameService error. CorrelationId: {CorrelationId}; Path: {Path}",
                context.TraceIdentifier, context.Request.Path);
            // 未预期异常不向调用方泄露内部实现细节；对外统一为可重试的上游不可用。
            return Results.Json(
                ApiFailure.Create("SERVICE_UNAVAILABLE", "游戏生成服务暂时不可用", context.TraceIdentifier),
                statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
        }));
    }

    // Gateway 密钥验证（/healthz、/readyz 豁免）
    public static void UseGalGameGatewayKey(this WebApplication app, string gatewayKey)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/healthz" || context.Request.Path == "/readyz")
            {
                await next();
                return;
            }
            if (!GalGameHttp.IsGateway(context, gatewayKey))
            {
                await GalGameHttp.Failure(context, 403, "FORBIDDEN", "该服务仅接受经 API Gateway 转发的请求").ExecuteAsync(context);
                return;
            }
            await next();
        });
    }
}
