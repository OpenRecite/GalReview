namespace CreditService.Application;

/// <summary>进程内轻量计数器（T10 告警信号源）：Credit 预授权释放失败次数。</summary>
public static class CreditServiceMetrics
{
    private static long _releaseFailures;
    public static long ReleaseFailures => Interlocked.Read(ref _releaseFailures);
    public static void RecordReleaseFailure() => Interlocked.Increment(ref _releaseFailures);

    public static string RenderPrometheus() =>
        "# HELP credit_release_failures_total Credit reservation release attempts that failed\n" +
        "# TYPE credit_release_failures_total counter\n" +
        $"credit_release_failures_total {ReleaseFailures}\n";
}
