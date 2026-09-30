namespace PracticeService.API;

/// <summary>轻量 Prometheus 文本指标端点（T10 告警信号）。</summary>
public static class MetricsEndpoints
{
    public static void MapServiceMetrics(this IEndpointRouteBuilder app)
    {
        app.MapGet("/metrics", () => Results.Text(
            PracticeService.Application.PracticeServiceMetrics.RenderPrometheus(),
            "text/plain; version=0.0.4; charset=utf-8"));
    }
}
