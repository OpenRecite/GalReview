// ============================================================================
// 启动恢复：将因服务重启而卡在 RUNNING/QUEUED 的生成任务标记为 FAILED
// ============================================================================

public static class GalGameStartupRecovery
{
    public static async Task RecoverStaleJobsAsync(WebApplication app, bool useMongoStore)
    {
        if (!useMongoStore) return;
        try
        {
            var store = app.Services.GetRequiredService<IGameStore>();
            var recovery = store.RecoverStaleJobs();
            if (recovery.FailedCount > 0)
            {
                var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GalGameService");
                startupLogger.LogWarning("Startup recovery: {Count} stale job(s) marked as FAILED", recovery.FailedCount);
                // 恢复置 FAILED 的作业仍持有 HELD 预授权；逐一释放，避免 credits 悬挂
                foreach (var failedJobId in recovery.FailedJobIds)
                {
                    try { await app.Services.GetRequiredService<IGameCreditBilling>().ReleaseAsync(failedJobId, CancellationToken.None); }
                    catch (Exception releaseError)
                    {
                        startupLogger.LogError(releaseError, "Unable to release credits for recovered job {GenerationId}", failedJobId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GalGameService");
            startupLogger.LogWarning(ex, "Startup recovery failed; stale jobs may remain in RUNNING/QUEUED state");
        }
    }
}
