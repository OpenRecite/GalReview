using Microsoft.Extensions.Configuration;

/// <summary>
/// 消费 <see cref="IngestionQueue"/>：串行执行解析任务，避免无界 Task.Run。
/// 失败任务按指数退避自动重试（<c>Ingestion:RetryBaseDelaySeconds</c> × 2^n），
/// 超过 <c>Ingestion:MaxAttempts</c> 后保持 FAILED（死信，带最后一次 Error，可经任务查询接口检索）。
/// 启动恢复在消费前执行：崩溃时卡在 QUEUED/RUNNING 的任务被显式置 FAILED。
/// </summary>
public sealed class IngestionWorker(
    IngestionQueue queue,
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    private readonly int _maxAttempts = Math.Clamp(configuration.GetValue("Ingestion:MaxAttempts", 3), 1, 10);
    private readonly int _retryBaseDelaySeconds = Math.Clamp(configuration.GetValue("Ingestion:RetryBaseDelaySeconds", 5), 1, 300);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // jobId → 到期时间；退避等待不占用队列，避免阻塞新任务消费
        var pendingRetries = new List<(string JobId, DateTimeOffset ReadyAt)>();
        try
        {
            try
            {
                await services.GetRequiredService<MongoFileStore>()
                    .RecoverIncompleteJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception recoveryError)
            {
                logger.LogError(recoveryError, "Startup ingestion recovery failed; continuing to consume queue.");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                DrainDueRetries(pendingRetries);

                if (queue.Reader.TryRead(out var jobId))
                {
                    await ProcessAsync(jobId, pendingRetries, stoppingToken);
                    continue;
                }

                if (pendingRetries.Count == 0)
                {
                    if (!await queue.Reader.WaitToReadAsync(stoppingToken)) break;
                    continue;
                }

                var nextReadyIn = pendingRetries.Min(retry => retry.ReadyAt) - DateTimeOffset.UtcNow;
                var wait = nextReadyIn > TimeSpan.Zero ? nextReadyIn : TimeSpan.FromMilliseconds(50);
                await Task.WhenAny(
                    queue.Reader.WaitToReadAsync(stoppingToken).AsTask(),
                    Task.Delay(wait, stoppingToken));
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
    }

    private void DrainDueRetries(List<(string JobId, DateTimeOffset ReadyAt)> pendingRetries)
    {
        var now = DateTimeOffset.UtcNow;
        for (var index = pendingRetries.Count - 1; index >= 0; index--)
        {
            if (pendingRetries[index].ReadyAt > now) continue;
            logger.LogInformation(
                "Re-enqueuing ingestion job {JobId} for retry.", pendingRetries[index].JobId);
            queue.Enqueue(pendingRetries[index].JobId);
            pendingRetries.RemoveAt(index);
        }
    }

    private async Task ProcessAsync(
        string jobId,
        List<(string JobId, DateTimeOffset ReadyAt)> pendingRetries,
        CancellationToken stoppingToken)
    {
        try
        {
            var store = services.GetRequiredService<IFileStore>();
            await store.ProcessJobAsync(jobId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error)
        {
            logger.LogError(error, "Ingestion job {JobId} failed in background worker.", jobId);
        }

        // 终态检查：FAILED 的任务要么按退避重排（AttemptCount < MaxAttempts），要么保持 FAILED 死信。
        var store2 = services.GetRequiredService<IFileStore>();
        var job = store2.GetJob(jobId);
        if (job is not { Status: "FAILED" }) return;

        if (!store2.TryRequeueForRetry(jobId, _maxAttempts))
        {
            logger.LogWarning(
                "Ingestion job {JobId} exhausted its retry budget ({Attempts} attempt(s)); kept as FAILED for inspection.",
                jobId,
                Math.Max(job.AttemptCount, 1));
            return;
        }

        var nextAttempt = job.AttemptCount + 1;
        var delay = TimeSpan.FromSeconds(_retryBaseDelaySeconds * Math.Pow(2, Math.Max(0, nextAttempt - 2)));
        pendingRetries.Add((jobId, DateTimeOffset.UtcNow + delay));
        logger.LogInformation(
            "Ingestion job {JobId} scheduled for retry #{Attempt} in {Seconds:0}s.",
            jobId,
            nextAttempt,
            delay.TotalSeconds);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        return base.StopAsync(cancellationToken);
    }
}
