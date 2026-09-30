namespace PracticeService.Application;

/// <summary>
/// 进程内轻量计数器（T10 告警信号源）。
/// 单实例自增，供 /metrics 输出 Prometheus 文本；无外部依赖。
/// </summary>
public static class PracticeServiceMetrics
{
    private static long _gradingsTotal;
    private static long _abstainedTotal;
    private static long _degradedTotal;

    public static long GradingsTotal => Interlocked.Read(ref _gradingsTotal);
    public static long AbstainedTotal => Interlocked.Read(ref _abstainedTotal);
    public static long DegradedTotal => Interlocked.Read(ref _degradedTotal);

    public static void RecordGrading(bool abstained, bool degraded)
    {
        Interlocked.Increment(ref _gradingsTotal);
        if (abstained) Interlocked.Increment(ref _abstainedTotal);
        if (degraded) Interlocked.Increment(ref _degradedTotal);
    }

    public static string RenderPrometheus()
    {
        return
            "# HELP practice_gradings_total Practice answer gradings by outcome\n" +
            "# TYPE practice_gradings_total counter\n" +
            $"practice_gradings_total {GradingsTotal}\n" +
            "# HELP practice_abstained_total Grading results that abstained (model unavailable/degraded)\n" +
            "# TYPE practice_abstained_total counter\n" +
            $"practice_abstained_total {AbstainedTotal}\n" +
            "# HELP practice_degraded_total Grading results marked degraded\n" +
            "# TYPE practice_degraded_total counter\n" +
            $"practice_degraded_total {DegradedTotal}\n";
    }
}
