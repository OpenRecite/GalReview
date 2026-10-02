using System.Threading.Channels;

/// <summary>
/// 进程内解析任务队列。比 Task.Run fire-and-forget 更可靠：
/// 可随 Host 生命周期取消，并在优雅关闭时排空已入队任务。
/// </summary>
public sealed class IngestionQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public void Enqueue(string jobId)
    {
        if (!_channel.Writer.TryWrite(jobId))
            throw new InvalidOperationException("Ingestion queue is completed.");
    }

    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>当前排队任务数；无界通道不支持 Count，返回 -1 表示未知。</summary>
    public int Depth => _channel.Reader.CanCount ? _channel.Reader.Count : -1;

    /// <summary>底层读取端：Worker 用它做 TryRead / WaitToReadAsync 的重试调度。</summary>
    public System.Threading.Channels.ChannelReader<string> Reader => _channel.Reader;

    public void Complete() => _channel.Writer.TryComplete();
}
