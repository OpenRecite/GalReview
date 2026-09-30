using System.Threading.Channels;

/// <summary>
/// 剧情生成工作队列：替代 Task.Run fire-and-forget，随 Host 生命周期取消并可排空。
/// </summary>
public sealed class GameGenerationQueue
{
    private readonly Channel<Func<CancellationToken, Task>> _channel =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

    public void Enqueue(Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!_channel.Writer.TryWrite(work))
            throw new InvalidOperationException("Game generation queue is completed.");
    }

    public IAsyncEnumerable<Func<CancellationToken, Task>> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public void Complete() => _channel.Writer.TryComplete();
}
