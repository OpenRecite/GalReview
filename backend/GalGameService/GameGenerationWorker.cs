/// <summary>
/// 串行消费生成队列。每个任务内部已有完整状态机与 credits 结算；此处只负责
/// 生命周期与异常兜底，避免无界 Task.Run。
/// </summary>
public sealed class GameGenerationWorker(
    GameGenerationQueue queue,
    ILogger<GameGenerationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var work in queue.ReadAllAsync(stoppingToken))
            {
                if (stoppingToken.IsCancellationRequested) break;
                try
                {
                    await work(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    logger.LogError(error, "Unhandled error in game generation work item.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        return base.StopAsync(cancellationToken);
    }
}
