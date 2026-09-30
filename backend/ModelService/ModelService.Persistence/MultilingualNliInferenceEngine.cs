using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using ModelService.Application;
using ModelService.Domain;

namespace ModelService.Persistence;

public sealed class MultilingualNliInferenceEngine : IFacetInferenceEngine, IInferenceLoadStats, IDisposable
{
    public const string Version = "multilingual-minilmv2-l6-mnli-xnli@0a71e92a985b6e1ad1828cf67ce9c459639c1dca+strict-synonym-v1";
    private const int MaximumTokens = 512;
    private const int BeginningOfSentenceId = 0;
    private const int PaddingId = 1;
    private const int EndOfSentenceId = 2;

    private readonly ModelAssetCatalog _assets;
    private readonly ILogger<MultilingualNliInferenceEngine> _logger;
    private readonly double _minimumTopProbability;
    private readonly double _minimumMargin;
    private readonly Lazy<ModelRuntime?> _runtime;
    /// <summary>同时进行的推理批次数上限，避免 ONNX CPU 被过多并发打满。</summary>
    private readonly SemaphoreSlim _batchGate;
    /// <summary>等待批次门的批次数上限；超出立即 503 + Retry-After，防止无限排队。</summary>
    private readonly int _maxPendingBatches;
    private readonly int _retryAfterSeconds;
    private readonly int _facetParallelism;
    private int _inflight;
    private int _pending;
    private long _completedBatches;

    public MultilingualNliInferenceEngine(
        ModelAssetCatalog assets,
        IConfiguration configuration,
        ILogger<MultilingualNliInferenceEngine> logger)
    {
        _assets = assets;
        _logger = logger;
        _minimumTopProbability = Math.Clamp(
            configuration.GetValue("Nli:MinimumTopProbability", 0.75d), 0d, 1d);
        _minimumMargin = Math.Clamp(
            configuration.GetValue("Nli:MinimumMargin", 0.20d), 0d, 1d);
        var cores = Math.Max(1, Environment.ProcessorCount);
        var maxBatches = Math.Clamp(
            configuration.GetValue("Nli:MaxConcurrentBatches", Math.Max(1, cores / 2)),
            1, cores);
        _batchGate = new SemaphoreSlim(maxBatches, maxBatches);
        _maxPendingBatches = Math.Clamp(
            configuration.GetValue("Nli:MaxPendingBatches", 64), 1, 4096);
        _retryAfterSeconds = Math.Clamp(
            configuration.GetValue("Nli:RetryAfterSeconds", 2), 1, 60);
        _facetParallelism = Math.Clamp(
            configuration.GetValue("Nli:FacetParallelism", Math.Min(4, cores)),
            1, 8);
        _runtime = new(CreateRuntime, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int InflightBatches => Volatile.Read(ref _inflight);
    public long CompletedBatches => Interlocked.Read(ref _completedBatches);

    /// <summary>已进入 InferAsync 但尚未获得批次门的批次数（排队深度）。</summary>
    public int QueuedBatches => Math.Max(0, Volatile.Read(ref _pending) - Volatile.Read(ref _inflight));

    public async Task<FacetInferenceBatch> InferAsync(
        string answer,
        IReadOnlyList<string> claims,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runtime = _runtime.Value;
        if (runtime is null)
            return new FacetInferenceBatch(false, Version, [], "NLI_MODEL_UNAVAILABLE");

        // 背压：排队深度超过上限时立即拒绝（503 + Retry-After），避免无限排队堆积
        var pending = Interlocked.Increment(ref _pending);
        if (pending > _maxPendingBatches)
        {
            Interlocked.Decrement(ref _pending);
            throw new InferenceOverloadedException(_maxPendingBatches, _retryAfterSeconds);
        }

        Interlocked.Increment(ref _inflight);
        try
        {
            await _batchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // ONNX CPU 推理是同步阻塞：offload 到线程池，避免占用 ASP.NET 请求线程
                var batch = await Task.Run(
                    () => RunFacets(runtime, answer, claims, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _completedBatches);
                return batch;
            }
            finally
            {
                _batchGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogError(error, "NLI facet inference failed.");
            return new FacetInferenceBatch(false, Version, [], "NLI_INFERENCE_FAILED");
        }
        finally
        {
            Interlocked.Decrement(ref _inflight);
            Interlocked.Decrement(ref _pending);
        }
    }

    private FacetInferenceBatch RunFacets(
        ModelRuntime runtime,
        string answer,
        IReadOnlyList<string> claims,
        CancellationToken cancellationToken)
    {
        var results = new InferenceFacet[claims.Count];
        if (claims.Count == 1 || _facetParallelism == 1)
        {
            for (var index = 0; index < claims.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results[index] = Adjudicate(runtime, answer, claims[index]);
            }
            return new FacetInferenceBatch(true, Version, results, null);
        }

        // InferenceSession.Run 线程安全；facet 级有界并行降低单题延迟
        Parallel.For(
            0,
            claims.Count,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _facetParallelism,
                CancellationToken = cancellationToken,
            },
            index =>
            {
                results[index] = Adjudicate(runtime, answer, claims[index]);
            });
        return new FacetInferenceBatch(true, Version, results, null);
    }

    private InferenceFacet Adjudicate(ModelRuntime runtime, string answer, string claim)
    {
        var raw = Infer(runtime, answer, claim);
        var rawVerdict = Classify(raw);
        if (rawVerdict == FacetVerdict.Omitted)
        {
            var rewrittenClaim = runtime.Synonyms.RewriteClaim(answer, claim);
            if (rewrittenClaim is not null)
            {
                var rewritten = Infer(runtime, answer, rewrittenClaim);
                var rewrittenVerdict = Classify(rewritten);
                if (rewrittenVerdict == FacetVerdict.Entailed)
                    return new(claim, rewrittenVerdict, rewritten[0], rewritten[1], rewritten[2]);
                if (rewrittenVerdict == FacetVerdict.Contradicted)
                    return new(claim, FacetVerdict.Indeterminate, rewritten[0], rewritten[1], rewritten[2]);
            }
        }
        return new(claim, rawVerdict, raw[0], raw[1], raw[2]);
    }

    private double[] Infer(ModelRuntime runtime, string answer, string claim)
    {
        var tokenIds = EncodePair(runtime.Tokenizer, answer, claim);
        var dimensions = new[] { 1, tokenIds.Length };
        var ids = new DenseTensor<long>(tokenIds.Select(id => (long)id).ToArray(), dimensions);
        var attention = new DenseTensor<long>(
            tokenIds.Select(id => id == PaddingId ? 0L : 1L).ToArray(), dimensions);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", ids),
            NamedOnnxValue.CreateFromTensor("attention_mask", attention)
        };
        if (runtime.Session.InputMetadata.ContainsKey("token_type_ids"))
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids",
                new DenseTensor<long>(new long[tokenIds.Length], dimensions)));

        using var output = runtime.Session.Run(inputs);
        var logits = output.First().AsEnumerable<float>()
            .Select(value => (double)value).Take(3).ToArray();
        if (logits.Length != 3 || logits.Any(value => !double.IsFinite(value)))
            throw new InvalidOperationException("NLI model returned invalid logits.");
        return Softmax(logits);
    }

    private FacetVerdict Classify(double[] probabilities)
    {
        var ordered = probabilities.OrderByDescending(value => value).ToArray();
        var label = Array.IndexOf(probabilities, probabilities.Max());
        return ordered[0] < _minimumTopProbability || ordered[0] - ordered[1] < _minimumMargin
            ? FacetVerdict.Indeterminate
            : label switch
            {
                0 => FacetVerdict.Entailed,
                1 => FacetVerdict.Omitted,
                2 => FacetVerdict.Contradicted,
                _ => FacetVerdict.Indeterminate
            };
    }

    private ModelRuntime? CreateRuntime()
    {
        if (!_assets.NliReady) return null;
        try
        {
            using var tokenizerStream = File.OpenRead(_assets.NliTokenizerPath);
            var tokenizer = SentencePieceTokenizer.Create(tokenizerStream, false, false);
            var session = new InferenceSession(_assets.NliModelPath);
            var synonyms = SynonymLexicon.Load(_assets.SynonymPath);
            return new(tokenizer, session, synonyms);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "NLI model could not be loaded.");
            return null;
        }
    }

    private static int[] EncodePair(
        SentencePieceTokenizer tokenizer,
        string premise,
        string hypothesis)
    {
        var premiseIds = tokenizer.EncodeToIds(premise, false, false, true, true)
            .Select(id => id + 1).ToList();
        var hypothesisIds = tokenizer.EncodeToIds(hypothesis, false, false, true, true)
            .Select(id => id + 1).ToList();
        var available = MaximumTokens - 4;
        while (premiseIds.Count + hypothesisIds.Count > available)
        {
            if (premiseIds.Count >= hypothesisIds.Count && premiseIds.Count > 1)
                premiseIds.RemoveAt(premiseIds.Count - 1);
            else if (hypothesisIds.Count > 1)
                hypothesisIds.RemoveAt(hypothesisIds.Count - 1);
            else break;
        }
        return [BeginningOfSentenceId, .. premiseIds, EndOfSentenceId,
            EndOfSentenceId, .. hypothesisIds, EndOfSentenceId];
    }

    private static double[] Softmax(IReadOnlyList<double> logits)
    {
        var maximum = logits.Max();
        var exponentials = logits.Select(logit => Math.Exp(logit - maximum)).ToArray();
        var total = exponentials.Sum();
        return exponentials.Select(value => value / total).ToArray();
    }

    public void Dispose()
    {
        // 先占满批次门，尽量等待进行中的 Parallel.For/Session.Run 结束，再释放 session
        try
        {
            var max = _batchGate.CurrentCount + _inflight;
            for (var i = 0; i < Math.Max(1, max); i++)
            {
                if (!_batchGate.Wait(TimeSpan.FromMilliseconds(200))) break;
            }
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }
        if (_runtime.IsValueCreated) _runtime.Value?.Session.Dispose();
        _batchGate.Dispose();
    }

    private sealed record ModelRuntime(
        SentencePieceTokenizer Tokenizer,
        InferenceSession Session,
        SynonymLexicon Synonyms);

    private sealed class SynonymLexicon(IReadOnlyList<string[]> groups)
    {
        public static SynonymLexicon Load(string path)
        {
            var groups = File.ReadLines(path)
                .Select(line =>
                {
                    var separator = line.IndexOf('=');
                    return separator < 0
                        ? []
                        : line[(separator + 1)..]
                            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Where(term => term.Length >= 2)
                            .Distinct(StringComparer.Ordinal)
                            .OrderByDescending(term => term.Length)
                            .ToArray();
                })
                .Where(group => group.Length >= 2)
                .ToArray();
            return new SynonymLexicon(groups);
        }

        public string? RewriteClaim(string answer, string claim)
        {
            var rewritten = claim;
            var changed = false;
            foreach (var group in groups)
            {
                var source = group.FirstOrDefault(term => rewritten.Contains(term, StringComparison.Ordinal));
                var target = group.FirstOrDefault(term => answer.Contains(term, StringComparison.Ordinal));
                if (source is null || target is null || source == target ||
                    answer.Contains(source, StringComparison.Ordinal))
                    continue;
                rewritten = rewritten.Replace(source, target, StringComparison.Ordinal);
                changed = true;
            }
            return changed ? rewritten : null;
        }
    }
}
