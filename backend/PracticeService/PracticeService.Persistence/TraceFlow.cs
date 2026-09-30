namespace PracticeService.Persistence;

/// <summary>
/// 请求级链路上下文（AsyncLocal）。由 API 层请求日志中间件写入，
/// 供出站 HTTP 客户端（GatewayClient / GatewayModelFacetAdjudicator）
/// 透传 X-Correlation-Id，实现跨服务一次定位。
/// 后台任务无 HttpContext 时可显式 Set，未设置则客户端生成新 id。
/// </summary>
public static class TraceFlow
{
    private static readonly AsyncLocal<string?> CurrentId = new();

    public static string? Current => CurrentId.Value;

    public static IDisposable? Begin(string traceId)
    {
        var previous = CurrentId.Value;
        CurrentId.Value = traceId;
        return new Releaser(previous);
    }

    private sealed class Releaser(string? previous) : IDisposable
    {
        public void Dispose() => CurrentId.Value = previous;
    }
}
