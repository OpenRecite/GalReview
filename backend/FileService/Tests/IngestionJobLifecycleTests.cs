using Xunit;

namespace FileService.Tests;

/// <summary>
/// 解析任务生命周期与 Worker 重试/死信语义（IFileStore.TryRequeueForRetry 契约）。
/// 覆盖：QUEUED→PROCESSING 置位、成功推进 READY、失败错误信封、FAILED→QUEUED 重排与 AttemptCount 死信。
/// </summary>
public sealed class IngestionJobLifecycleTests
{
    [Fact]
    public async Task CreateJob_claims_material_as_processing_with_latest_job_id()
    {
        var store = TestSupport.NewStore();
        var owner = Guid.NewGuid().ToString();
        var stored = await store.CreateAsync(owner, TestSupport.File("a.txt", "text/plain", "hello"), "A", null, CancellationToken.None);
        var id = stored.Material.MaterialId;

        var job = store.CreateJob(id, "files-text-v1", enableOcr: false, "standard");
        Assert.NotNull(job);
        Assert.Equal("QUEUED", job!.Status);
        Assert.Equal(0, job.Progress);
        Assert.Equal("standard", job.OcrMode);

        var material = store.GetMaterial(id);
        Assert.Equal("PROCESSING", material!.Status);
        Assert.Equal(job.JobId, material.LatestIngestionJobId);
    }

    [Fact]
    public async Task ProcessJob_text_input_publishes_ready_text_before_terminal_state()
    {
        var store = TestSupport.NewStore();
        var owner = Guid.NewGuid().ToString();
        var stored = await store.CreateAsync(owner, TestSupport.File("a.txt", "text/plain", "line1\r\nline2"), "A", null, CancellationToken.None);
        var id = stored.Material.MaterialId;
        var job = store.CreateJob(id, "files-text-v1", false, "standard")!;

        await store.ProcessJobAsync(job.JobId, CancellationToken.None);

        var finished = store.GetJob(job.JobId)!;
        Assert.Equal("SUCCEEDED", finished.Status);
        Assert.Equal(100, finished.Progress);
        Assert.Null(finished.Error);

        var material = store.GetMaterial(id)!;
        Assert.Equal("READY", material.Status);

        // 契约 §5.2.1：CRLF 规范为 LF，NFC，textChecksum 为规范化文本的 SHA-256
        var document = store.GetExtractedText(id);
        Assert.NotNull(document);
        Assert.Equal("line1\nline2", document!.Text);
        Assert.Equal("utf-8", document.Encoding);
        Assert.Equal("NFC", document.Normalization);
        Assert.Equal("LF", document.LineEnding);
        Assert.Equal("1", document.SourceMapVersion);
        Assert.Equal(document.Text.Length, document.TextLength);
        Assert.Single(document.SourceMap);
        Assert.Single(document.Blocks);
        Assert.Equal("PARAGRAPH", document.Blocks[0].Kind);
    }

    [Fact]
    public async Task ProcessJob_non_text_input_fails_with_extraction_error_envelope()
    {
        var store = TestSupport.NewStore();
        var owner = Guid.NewGuid().ToString();
        var stored = await store.CreateAsync(owner, TestSupport.File("a.pdf", "application/pdf", "x"), "A", null, CancellationToken.None);
        var id = stored.Material.MaterialId;
        var job = store.CreateJob(id, "files-text-v1", false, "standard")!;

        await store.ProcessJobAsync(job.JobId, CancellationToken.None);

        var failed = store.GetJob(job.JobId)!;
        Assert.Equal("FAILED", failed.Status);
        Assert.Equal("MATERIAL_TEXT_EXTRACTION_FAILED", failed.Error!.Code);
        Assert.Equal("FAILED", store.GetMaterial(id)!.Status);
        Assert.Null(store.GetExtractedText(id));
    }

    [Fact]
    public async Task TryRequeueForRetry_requeues_failed_job_and_increments_attempt_count()
    {
        var store = TestSupport.NewStore();
        var owner = Guid.NewGuid().ToString();
        var stored = await store.CreateAsync(owner, TestSupport.File("a.pdf", "application/pdf", "x"), "A", null, CancellationToken.None);
        var job = store.CreateJob(stored.Material.MaterialId, "files-text-v1", false, "standard")!;
        await store.ProcessJobAsync(job.JobId, CancellationToken.None);
        Assert.Equal("FAILED", store.GetJob(job.JobId)!.Status);

        Assert.True(store.TryRequeueForRetry(job.JobId, maxAttempts: 3));
        var requeued = store.GetJob(job.JobId)!;
        Assert.Equal("QUEUED", requeued.Status);
        Assert.Equal(0, requeued.Progress);
        Assert.Null(requeued.Error);
        Assert.Equal(1, requeued.AttemptCount);
    }

    [Fact]
    public async Task TryRequeueForRetry_dead_letters_after_max_attempts()
    {
        var store = TestSupport.NewStore();
        var owner = Guid.NewGuid().ToString();
        var stored = await store.CreateAsync(owner, TestSupport.File("a.pdf", "application/pdf", "x"), "A", null, CancellationToken.None);
        var job = store.CreateJob(stored.Material.MaterialId, "files-text-v1", false, "standard")!;

        // AttemptCount 记录的是“重排次数”；首次失败后 AttemptCount=0。
        // maxAttempts=3：允许 3 次重排（共 4 次执行），第 4 次失败后死信。
        for (var round = 0; round < 4; round++)
        {
            await store.ProcessJobAsync(job.JobId, CancellationToken.None);
            Assert.Equal("FAILED", store.GetJob(job.JobId)!.Status);
            var canRetry = store.TryRequeueForRetry(job.JobId, maxAttempts: 3);
            Assert.Equal(round < 3, canRetry);
        }

        var dead = store.GetJob(job.JobId)!;
        Assert.Equal("FAILED", dead.Status);
        Assert.Equal(3, dead.AttemptCount);
        Assert.Equal("MATERIAL_TEXT_EXTRACTION_FAILED", dead.Error!.Code);
    }

    [Fact]
    public void TryRequeueForRetry_rejects_non_failed_or_unknown_jobs()
    {
        var store = TestSupport.NewStore();
        var owner = Guid.NewGuid().ToString();
        var stored = store.CreateAsync(owner, TestSupport.File("a.txt", "text/plain", "x"), "A", null, CancellationToken.None).GetAwaiter().GetResult();
        var job = store.CreateJob(stored.Material.MaterialId, "files-text-v1", false, "standard")!;

        Assert.False(store.TryRequeueForRetry(job.JobId, maxAttempts: 3)); // QUEUED 不可重排
        Assert.False(store.TryRequeueForRetry("missing-job", maxAttempts: 3));
    }

    [Fact]
    public async Task GetLatestJob_returns_most_recent_job_for_material()
    {
        var store = TestSupport.NewStore();
        var owner = Guid.NewGuid().ToString();
        var stored = await store.CreateAsync(owner, TestSupport.File("a.txt", "text/plain", "hello"), "A", null, CancellationToken.None);
        var id = stored.Material.MaterialId;

        var first = store.CreateJob(id, "files-text-v1", false, "standard")!;
        await store.ProcessJobAsync(first.JobId, CancellationToken.None);
        var second = store.CreateJob(id, "files-text-v2", false, "standard")!;

        Assert.Equal(second.JobId, store.GetLatestJob(id)!.JobId);
        Assert.Equal(first.JobId, store.GetJob(first.JobId)!.JobId);
    }
}
