// ============================================================================
// 端点 1：POST /api/v1/game-generations — 创建游戏包生成任务
// 契约 §7.3.1 URGENT：同步经 Gateway 读取 PlanGraph 并校验 snapshotVersion。
//   - snapshot 不匹配 → 422 REVIEW_PLAN_SNAPSHOT_MISMATCH
//   - reviewPlan 不存在 → 422 REVIEW_PLAN_NOT_FOUND
//   - 上游不可用 → 503 SERVICE_UNAVAILABLE
//   - 校验通过 → 202 Accepted，后台异步生成
// ============================================================================
// 端点 2：GET /api/v1/game-generations/{generationId} — 查询生成任务
// ============================================================================

public static class GameGenerationEndpoints
{
    public static void MapGameGenerationEndpoints(
        this WebApplication app,
        string gatewayKey,
        bool isMockMode,
        bool narrativeEnabled,
        NarrativeGenerationOptions narrativeOptions)
    {
        app.MapPost("/api/v1/game-generations", async (GameGenerationRequest request, HttpContext c, IGameStore store, PlanGraphClient planClient, NarrativeGenerationService narrativeService, PackageVoiceService voiceService, IGameCreditBilling billing) =>
        {
            var userId = GalGameHttp.GatewayUser(c, gatewayKey);
            if (userId is null) return GalGameHttp.Failure(c, 401, "AUTH_REQUIRED", "需要网关认证的用户身份。");

            // 请求验证
            if (!GalGameHttp.IsUuidV4(request.ReviewPlanId))
                return GalGameHttp.Failure(c, 400, "VALIDATION_ERROR", "reviewPlanId 必须为 UUID v4。");
            if (string.IsNullOrWhiteSpace(request.SnapshotVersion))
                return GalGameHttp.Failure(c, 400, "VALIDATION_ERROR", "snapshotVersion 不能为空。");
            if (string.IsNullOrWhiteSpace(request.Locale))
                return GalGameHttp.Failure(c, 400, "VALIDATION_ERROR", "locale 不能为空。");

            var traceId = c.TraceIdentifier;
            var logger = c.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GalGameService.Generation");
            if (!Guid.TryParse(userId, out var ownerUserId))
                return GalGameHttp.Failure(c, 401, "AUTH_REQUIRED", "用户身份格式无效。");

            // §7.3.1 URGENT：同步经 Gateway 读取不可变 PlanGraph，校验 snapshotVersion
            PlanGraphFetchResult fetchResult;
            try
            {
                fetchResult = await planClient.GetGraphAsync(
                    request.ReviewPlanId, request.SnapshotVersion, traceId, c.RequestAborted, mockOwnerUserId: ownerUserId);
            }

            catch (OperationCanceledException) when (c.RequestAborted.IsCancellationRequested)
            {
                return GalGameHttp.Failure(c, 499, "CLIENT_CLOSED_REQUEST", "客户端断开连接");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unable to read PlanGraph. CorrelationId: {CorrelationId}; ReviewPlanId: {ReviewPlanId}",
                    traceId, request.ReviewPlanId);
                return GalGameHttp.Failure(c, 503, "SERVICE_UNAVAILABLE", "知识图谱服务暂时不可用");
            }

            if (fetchResult.Status == PlanGraphFetchStatus.SnapshotMismatch)
                return GalGameHttp.Failure(c, 422, "REVIEW_PLAN_SNAPSHOT_MISMATCH", fetchResult.Detail ?? "snapshot 不匹配");
            if (fetchResult.Status == PlanGraphFetchStatus.NotFound)
                return GalGameHttp.Failure(c, 422, "REVIEW_PLAN_NOT_FOUND", fetchResult.Detail ?? "复习计划不存在");
            if (fetchResult.Status == PlanGraphFetchStatus.InvalidRequest)
                return GalGameHttp.Failure(c, 400, "VALIDATION_ERROR", fetchResult.Detail ?? "复习计划图请求不符合契约");
            if (fetchResult.Status == PlanGraphFetchStatus.UpstreamContractInvalid)
                throw new UpstreamContractException(fetchResult.Detail ?? "KnowledgeService 响应不符合契约");
            if (fetchResult.Status == PlanGraphFetchStatus.Unavailable)
                return GalGameHttp.Failure(c, 503, "SERVICE_UNAVAILABLE", fetchResult.Detail ?? "知识图谱服务不可用");
            if (fetchResult.Status != PlanGraphFetchStatus.Success || fetchResult.Graph is null)
                throw new UpstreamContractException("PlanGraph 读取结果状态不完整");

            var graph = fetchResult.Graph;
            if (graph.OwnerUserId != ownerUserId)
                return GalGameHttp.Failure(c, 422, "REVIEW_PLAN_NOT_FOUND", "复习计划不存在或不可用于当前用户。");

            // PlanGraph 已校验且属于当前用户，创建生成任务
            GameGenerationJob job;
            try
            {
                job = store.CreateJob(userId, request);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unable to create generation job. CorrelationId: {CorrelationId}; ReviewPlanId: {ReviewPlanId}",
                    traceId, request.ReviewPlanId);
                return GalGameHttp.Failure(c, 503, "SERVICE_UNAVAILABLE", "游戏生成任务暂时不可用");
            }

            var estimatedUnits = narrativeEnabled
                ? Math.Max(1L,
                    ((graph.Nodes ?? []).Sum(node => (long)((node.Title?.Length ?? 0) + (node.Summary?.Length ?? 0))) / 2L
                        + 2_048L
                        + Math.Clamp(narrativeOptions.MaxOutputTokens, 1_000, 32_000))
                    * Math.Clamp(narrativeOptions.MaxDraftAttempts, 1, 3)
                    * Math.Clamp(narrativeOptions.MaxProviderAttempts, 1, 4))
                : 1L;
            try
            {
                await billing.ReserveAsync(ownerUserId, job.GenerationId, estimatedUnits, c.RequestAborted);
            }
            catch (CreditBillingException credit)
            {
                // 上游 details 由 System.Text.Json 解析为 JsonElement；MongoDB 的安全 ObjectSerializer
                // 不接受该运行时类型。任务仅持久化稳定的错误码与消息，即时响应仍完整返回购买信息。
                store.TryTransitionJob(job.GenerationId, JobStatus.QUEUED, j => j with { Status = JobStatus.FAILED, Error = new ApiError(credit.Code, credit.Message, new Dictionary<string, string>()) });
                return GalGameHttp.Failure(c, credit.StatusCode, credit.Code, credit.Message, credit.Details);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unable to reserve credits. CorrelationId: {CorrelationId}", traceId);
                store.TryTransitionJob(job.GenerationId, JobStatus.QUEUED, j => j with { Status = JobStatus.FAILED, Error = new ApiError("SERVICE_UNAVAILABLE", "credits 服务暂时不可用", new Dictionary<string, string>()) });
                return GalGameHttp.Failure(c, 503, "SERVICE_UNAVAILABLE", "credits 服务暂时不可用");
            }

            // 入队到 GameGenerationQueue，由 GameGenerationWorker 随 Host 生命周期消费
            // 从根 Provider 取单例，避免请求结束销毁 scoped 服务
            // 闭包提为方法级局部变量：入队失败时可在 catch 中按退避重试同一工作项
            Func<CancellationToken, Task>? generationWork = null;
            try
            {
                var generationQueue = app.Services.GetRequiredService<GameGenerationQueue>();
                generationWork = async workToken =>
                {
                // 后台执行时已离开 HTTP 上下文，显式挂回原请求 traceId，保证 credits 调用可串联
                using var workTrace = GalGameTraceFlow.Begin(traceId);
                var reservationSettled = false;
                try
                {
                    // 原子状态转换：QUEUED → RUNNING
                    if (store.TryTransitionJob(job.GenerationId, JobStatus.QUEUED,
                        j => j with { Status = JobStatus.RUNNING, Progress = 5, AttemptCount = j.AttemptCount + 1 }) is null)
                    {
                        logger.LogWarning("Job {GenerationId} was not in QUEUED state, skipping generation", job.GenerationId);
                        return;
                    }

                    void ReportProgress(int progress)
                    {
                        try
                        {
                            store.TryTransitionJob(job.GenerationId, JobStatus.RUNNING, current =>
                                current.Progress >= progress ? current : current with { Progress = progress });
                        }
                        catch (Exception progressError)
                        {
                            // 进度写入是观测信息，不应破坏已经在进行的剧情生成。
                            logger.LogWarning(progressError,
                                "Unable to persist progress {Progress} for job {GenerationId}",
                                progress,
                                job.GenerationId);
                        }
                    }

                    logger.LogInformation("Job {GenerationId} started generating. Style={Style}, Difficulty={Difficulty}",
                        job.GenerationId, request.Style, request.Difficulty);

                    // Mock 固定返回同一套原创演示剧情；仍保留请求的计划与快照字段，
                    // 使包的溯源、任务查询和权限边界继续符合 §7.1 / §7.3.1。
                    // 非 Mock 才执行真实的骨架生成与可选叙事模型重写。
                    GamePackage package;
                    long actualTokenUnits;
                    if (isMockMode)
                    {
                        package = MockStoryPackageFactory.Create(request);
                        actualTokenUnits = 0;
                        ReportProgress(85);
                    }
                    else
                    {
                        var generation = await narrativeService.GenerateAsync(
                            graph,
                            request,
                            userId,
                            ReportProgress);
                        package = generation.Package;
                        actualTokenUnits = generation.TotalTokens;
                    }

                    ReportProgress(86);
                    var voiceResult = await voiceService.SynthesizeAsync(
                        package,
                        ReportProgress);
                    package = voiceResult.Package;
                    ReportProgress(94);
                    var checksum = GamePackageValidator.ComputeChecksum(package);
                    ReportProgress(95);
                    var manifest = new GamePackageManifest(
                        package.PackageId, package.SchemaVersion, package.GeneratorVersion,
                        package.ReviewPlanId, package.SnapshotVersion, package.EntrySceneId,
                        package.Scenes.Length, checksum,
                        $"/api/v1/game-packages/{package.PackageId}/content",
                        userId, DateTimeOffset.UtcNow);

                    foreach (var audio in voiceResult.AudioAssets)
                        store.SaveAudio(audio);
                    store.SavePackage(package, manifest, userId);

                    // A failed persistence step must release the reservation instead of
                    // charging for a package that the user cannot retrieve.
                    await billing.SettleAsync(job.GenerationId, actualTokenUnits, CancellationToken.None);
                    reservationSettled = true;
                    ReportProgress(97);

                    // 原子状态转换：RUNNING → SUCCEEDED
                    store.TryTransitionJob(job.GenerationId, JobStatus.RUNNING,
                        j => j with { Status = JobStatus.SUCCEEDED, Progress = 100, PackageId = package.PackageId });

                    logger.LogInformation("Job {GenerationId} succeeded. PackageId={PackageId}, Scenes={SceneCount}",
                        job.GenerationId, package.PackageId, package.Scenes.Length);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Job {GenerationId} failed during generation", job.GenerationId);
                    if (!reservationSettled)
                    {
                        try { await billing.ReleaseAsync(job.GenerationId, CancellationToken.None); }
                        catch (Exception releaseError) { logger.LogError(releaseError, "Unable to release credits for failed job {GenerationId}", job.GenerationId); }
                    }
                    store.TryTransitionJob(job.GenerationId, JobStatus.RUNNING,
                        j => j with { Status = JobStatus.FAILED, Error = new ApiError("INTERNAL_ERROR", "生成任务处理失败，请稍后重试或联系支持团队", new Dictionary<string, string>()) });
                }
                };
                generationQueue.Enqueue(generationWork);
            }
            catch (Exception enqueueError)
            {
                // 入队失败（如队列瞬时不可用）：预授权仍持有，短退避内联重试；全部失败才释放并置 FAILED
                logger.LogError(enqueueError, "Unable to enqueue generation job {GenerationId}; retrying with backoff", job.GenerationId);
                var enqueued = false;
                for (var retryAttempt = 1; retryAttempt <= 3 && !enqueued && generationWork is not null; retryAttempt++)
                {
                    if (retryAttempt > 1)
                    {
                        try { await Task.Delay(TimeSpan.FromSeconds(2 * (retryAttempt - 1)), c.RequestAborted); }
                        catch (OperationCanceledException) { break; }
                    }
                    try
                    {
                        app.Services.GetRequiredService<GameGenerationQueue>().Enqueue(generationWork!);
                        enqueued = true;
                    }
                    catch (Exception retryError)
                    {
                        logger.LogWarning(retryError, "Enqueue retry #{Attempt} failed for {GenerationId}", retryAttempt, job.GenerationId);
                    }
                }
                if (enqueued)
                    return Results.Accepted($"/api/v1/game-generations/{job.GenerationId}", ApiSuccess.Create(job, c.TraceIdentifier));

                logger.LogError("Enqueue retries exhausted for generation job {GenerationId}", job.GenerationId);
                try { await billing.ReleaseAsync(job.GenerationId, CancellationToken.None); }
                catch (Exception releaseError) { logger.LogError(releaseError, "Unable to release credits after enqueue failure for {GenerationId}", job.GenerationId); }
                store.TryTransitionJob(job.GenerationId, JobStatus.QUEUED,
                    j => j with { Status = JobStatus.FAILED, Error = new ApiError("SERVICE_UNAVAILABLE", "生成队列暂不可用，请稍后重试", new Dictionary<string, string>()) });
                return GalGameHttp.Failure(c, 503, "SERVICE_UNAVAILABLE", "生成队列暂不可用，请稍后重试");
            }

            return Results.Accepted($"/api/v1/game-generations/{job.GenerationId}", ApiSuccess.Create(job, c.TraceIdentifier));
        });

        app.MapGet("/api/v1/game-generations/{generationId}", (string generationId, HttpContext c, IGameStore store) =>
        {
            var userId = GalGameHttp.GatewayUser(c, gatewayKey);
            if (userId is null) return GalGameHttp.Failure(c, 401, "AUTH_REQUIRED", "需要网关认证的用户身份。");
            if (!GalGameHttp.TryParseUuidV4(generationId, out var id))
                return GalGameHttp.Failure(c, 400, "VALIDATION_ERROR", "generationId 必须为 UUID v4。");
            var job = store.GetJob(id);
            if (job is null || job.OwnerUserId != userId)
                return GalGameHttp.Failure(c, 404, "RESOURCE_NOT_FOUND", "生成任务不存在。");
            return Results.Ok(ApiSuccess.Create(job, c.TraceIdentifier));
        });
    }
}
