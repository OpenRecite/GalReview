using MediatR;
using ModelService.Domain;

namespace ModelService.Application;

public sealed record AdjudicateFacetsCommand(
    string Answer,
    IReadOnlyList<string> Claims) : IRequest<FacetInferenceBatch>;

public sealed class AdjudicateFacetsHandler(IFacetInferenceEngine engine) :
    IRequestHandler<AdjudicateFacetsCommand, FacetInferenceBatch>
{
    public Task<FacetInferenceBatch> Handle(
        AdjudicateFacetsCommand request,
        CancellationToken cancellationToken)
    {
        var input = InferenceRules.Validate(request.Answer, request.Claims);
        return engine.InferAsync(input.Answer, input.Claims, cancellationToken);
    }
}

public sealed record GetModelReadinessQuery : IRequest<ModelReadiness>;
public sealed record ModelReadiness(
    string Status,
    IReadOnlyList<ModelAssetState> Models,
    InferenceLoadSnapshot? Load = null);

public sealed record InferenceLoadSnapshot(int InflightBatches, long CompletedBatches, int QueuedBatches);

/// <summary>推理队列已满：调用方应按 Retry-After 退避后重试。</summary>
public sealed class InferenceOverloadedException(int queuedLimit, int retryAfterSeconds)
    : Exception($"Inference queue is full; at most {queuedLimit} batches may be pending.")
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

public sealed class GetModelReadinessHandler(
    IModelAssetStatusReader assets,
    IInferenceLoadStats? loadStats = null) :
    IRequestHandler<GetModelReadinessQuery, ModelReadiness>
{
    public Task<ModelReadiness> Handle(
        GetModelReadinessQuery request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var states = assets.Inspect();
        var ready = states.Where(state => state.Required)
            .All(state => state.Status == "READY");
        InferenceLoadSnapshot? load = loadStats is null
            ? null
            : new(loadStats.InflightBatches, loadStats.CompletedBatches, loadStats.QueuedBatches);
        return Task.FromResult(new ModelReadiness(ready ? "ready" : "not-ready", states, load));
    }
}
