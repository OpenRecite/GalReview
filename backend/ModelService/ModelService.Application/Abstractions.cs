using ModelService.Domain;

namespace ModelService.Application;

public interface IFacetInferenceEngine
{
    Task<FacetInferenceBatch> InferAsync(
        string answer,
        IReadOnlyList<string> claims,
        CancellationToken cancellationToken);
}

/// <summary>推理负载指标，供 /readyz 与运维观测。</summary>
public interface IInferenceLoadStats
{
    int InflightBatches { get; }
    long CompletedBatches { get; }

    /// <summary>正在等待批次门（排队中）的批次数。</summary>
    int QueuedBatches { get; }
}

public interface IModelAssetStatusReader
{
    IReadOnlyList<ModelAssetState> Inspect();
}
