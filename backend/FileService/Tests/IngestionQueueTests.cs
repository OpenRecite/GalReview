using System.Threading.Channels;
using Xunit;

namespace FileService.Tests;

/// <summary>进程内解析队列语义：入队/读取/排空/Complete 后拒收。</summary>
public sealed class IngestionQueueTests
{
    [Fact]
    public async Task Enqueued_job_ids_come_out_in_fifo_order()
    {
        var queue = new IngestionQueue();
        queue.Enqueue("job-a");
        queue.Enqueue("job-b");
        queue.Enqueue("job-c");

        Assert.Equal("job-a", await queue.Reader.ReadAsync());
        Assert.Equal("job-b", await queue.Reader.ReadAsync());
        Assert.Equal("job-c", await queue.Reader.ReadAsync());
    }

    [Fact]
    public void TryRead_returns_false_when_empty()
    {
        var queue = new IngestionQueue();
        Assert.False(queue.Reader.TryRead(out _));
        queue.Enqueue("job-1");
        Assert.True(queue.Reader.TryRead(out var jobId));
        Assert.Equal("job-1", jobId);
        Assert.False(queue.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ReadAllAsync_yields_items_until_completed()
    {
        var queue = new IngestionQueue();
        queue.Enqueue("a");
        queue.Enqueue("b");

        var seen = new List<string>();
        queue.Complete();
        await foreach (var jobId in queue.ReadAllAsync(CancellationToken.None))
            seen.Add(jobId);

        Assert.Equal(new[] { "a", "b" }, seen);
    }

    [Fact]
    public void Enqueue_after_Complete_throws()
    {
        var queue = new IngestionQueue();
        queue.Complete();
        Assert.Throws<InvalidOperationException>(() => queue.Enqueue("late"));
    }

    [Fact]
    public void Complete_is_idempotent()
    {
        var queue = new IngestionQueue();
        queue.Complete();
        queue.Complete();
    }

    [Fact]
    public async Task Concurrent_writers_all_land_in_the_queue()
    {
        var queue = new IngestionQueue();
        var ids = Enumerable.Range(0, 50).Select(i => $"job-{i}").ToArray();
        Parallel.ForEach(ids, id => queue.Enqueue(id));

        var seen = new HashSet<string>();
        while (queue.Reader.TryRead(out var jobId))
            Assert.True(seen.Add(jobId), $"duplicate job {jobId}");

        Assert.Equal(ids.Length, seen.Count);
        await Task.CompletedTask;
    }
}
