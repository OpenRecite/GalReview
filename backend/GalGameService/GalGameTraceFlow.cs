/// <summary>
/// 请求级链路上下文（AsyncLocal）。由 RequestLogging 中间件写入，
/// 供 CreditBillingClient / PlanGraphClient 等出站调用透传 X-Correlation-Id。
/// 后台任务（GameGenerationWorker）无 HttpContext 时可显式 Begin。
/// </summary>
public static class GalGameTraceFlow
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
