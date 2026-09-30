using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PracticeService.Application;
using PracticeService.Domain;

namespace PracticeService.Persistence;

public sealed class ReciteQuestionGenerator(
    IHttpClientFactory clients,
    IConfiguration configuration,
    ILogger<ReciteQuestionGenerator> logger) : IPracticeQuestionGenerator
{
    private const int MaximumQuestions = 1000;
    internal static readonly PracticeQuestionKind[] ClassicKinds =
    [
        PracticeQuestionKind.SingleChoice,
        PracticeQuestionKind.FillBlank,
        PracticeQuestionKind.TermDefinition,
        PracticeQuestionKind.Essay
    ];
    private readonly ModelQuestionClient _model = new(clients, configuration);

    private int Parallelism => Math.Clamp(configuration.GetValue("QuestionGeneration:Parallelism", 4), 1, 8);

    public QuestionGenerationEstimate Estimate(QuestionGenerationInput input)
    {
        Validate(input);
        var extraction = GroundedQuestionExtractor.ExtractGroundedQuestions(input);
        var chunks = QuestionChunker.BuildChunks(input.Materials, input.Points).Count(chunk => chunk.Points.Count > 0 && !QuestionChunker.IsCoveredChunk(chunk, extraction.CoveredRanges));
        var target = ResolveTargetCount(input, extraction.Drafts.Count, chunks, extraction.IsExplicitQuestionBank);
        if (extraction.IsExplicitQuestionBank || extraction.Drafts.Count >= target)
            return new(target, 1, extraction.IsExplicitQuestionBank ? "SOURCE_EXTRACTION" : "HYBRID_EXTRACTION");

        var sourceCharacters = input.Materials.Sum(material => (long)material.Text.Length);
        var maximumTokens = checked(Math.Max(1L, sourceCharacters + target * 1400L));
        return new(target, maximumTokens, extraction.Drafts.Count == 0 ? "GROUNDED_GENERATION" : "HYBRID_GENERATION");
    }

    public async Task<QuestionGenerationOutput> GenerateAsync(QuestionGenerationInput input, CancellationToken cancellationToken)
    {
        Validate(input);
        var extraction = GroundedQuestionExtractor.ExtractGroundedQuestions(input);
        var allChunks = QuestionChunker.BuildChunks(input.Materials, input.Points)
            .Where(chunk => chunk.Points.Count > 0 && !QuestionChunker.IsCoveredChunk(chunk, extraction.CoveredRanges))
            .ToArray();
        var target = ResolveTargetCount(input, extraction.Drafts.Count, allChunks.Length, extraction.IsExplicitQuestionBank);
        var drafts = extraction.Drafts.Take(target).ToList();
        var diagnostics = extraction.Diagnostics.ToList();

        if (extraction.IsExplicitQuestionBank || drafts.Count >= target)
            return new(drafts, diagnostics, 1, extraction.IsExplicitQuestionBank ? "SOURCE_EXTRACTION" : "HYBRID_EXTRACTION");

        if (!_model.IsConfigured)
        {
            diagnostics.Add(new(null, "QUESTION_MODEL_NOT_CONFIGURED",
                "资料中没有足够的可直接核对题目；QuestionGeneration:ApiKey 未配置，未使用模板猜测题目。", false));
            return new(drafts, diagnostics, 1, drafts.Count == 0 ? "MODEL_REQUIRED" : "HYBRID_EXTRACTION");
        }

        var remaining = target - drafts.Count;
        var chunks = allChunks
            .Take((int)Math.Ceiling(remaining / 8d) + 2)
            .ToArray();
        if (chunks.Length == 0)
        {
            diagnostics.Add(new(null, "KNOWLEDGE_POINT_SOURCE_NOT_FOUND",
                "没有找到同时含资料证据与本册知识点的正文分片，未猜测贴签。", false));
            return new(drafts, diagnostics, 1, drafts.Count == 0 ? "MODEL_REQUIRED" : "HYBRID_EXTRACTION");
        }

        var outputs = new GeneratedChunk?[chunks.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, chunks.Length), new ParallelOptions
        {
            MaxDegreeOfParallelism = Parallelism,
            CancellationToken = cancellationToken
        }, async (index, ct) =>
        {
            try { outputs[index] = await _model.GenerateChunkAsync(chunks[index], input, ct); }
            catch (Exception error) when (error is not OperationCanceledException and not PracticeDomainException)
            {
                logger.LogWarning(error, "Question generation chunk failed for material {MaterialId} at {Offset}.", chunks[index].Material.MaterialId, chunks[index].StartOffset);
                outputs[index] = new([], [new(chunks[index].Material.MaterialId, "QUESTION_GENERATION_CHUNK_FAILED", error.Message, true)], 0);
            }
        });

        long actualTokens = 0;
        var seenQuestions = drafts.Select(QuestionDedupKey).ToHashSet(StringComparer.Ordinal);
        foreach (var output in outputs.Where(output => output is not null).Cast<GeneratedChunk>())
        {
            actualTokens += output.TokenUnits;
            diagnostics.AddRange(output.Diagnostics);
            foreach (var draft in output.Drafts)
            {
                if (drafts.Count >= target) break;
                if (seenQuestions.Add(QuestionDedupKey(draft))) drafts.Add(draft);
            }
        }
        if (drafts.Count == 0)
            diagnostics.Add(new(null, "NO_VERIFIED_QUESTIONS", "没有题目通过来源、答案、题型和知识点的全部校验门禁。", false));
        return new(drafts, diagnostics, Math.Max(1, actualTokens), extraction.Drafts.Count == 0 ? "GROUNDED_GENERATION" : "HYBRID_GENERATION");
    }

    private static void Validate(QuestionGenerationInput input)
    {
        if (!string.Equals(input.GeneratorVersion, "recite-question-v2", StringComparison.Ordinal))
            throw new PracticeDomainException(400, "GENERATOR_VERSION_UNSUPPORTED", "generatorVersion 当前只支持 recite-question-v2。");
        if (input.Materials.Count == 0) throw new PracticeDomainException(422, "PROJECT_MATERIALS_REQUIRED", "研习册至少需要一份资料。");
        if (input.Points.Count == 0) throw new PracticeDomainException(422, "PLAN_TARGETS_EMPTY", "整册知识点快照为空，无法为题目标记知识点。");
        if (input.RequestedTargetCount is < 1 or > MaximumQuestions)
            throw new PracticeDomainException(400, "VALIDATION_ERROR", $"targetCount 必须在 1-{MaximumQuestions} 范围内，或省略以自动建库。");
        var unsupported = input.Kinds.Except(ClassicKinds).ToArray();
        if (unsupported.Length > 0)
            throw new PracticeDomainException(400, "QUESTION_KIND_UNSUPPORTED", "自动建库仅支持单选、填空、名词解释和简答/论述；判断题仍可手工录入或导入。", new { kinds = unsupported });
    }

    private static int ResolveTargetCount(QuestionGenerationInput input, int extractedCount, int groundedChunkCount, bool explicitQuestionBank)
    {
        if (input.RequestedTargetCount is int requested) return requested;
        if (explicitQuestionBank) return Math.Clamp(extractedCount, 1, MaximumQuestions);
        return Math.Clamp(extractedCount + groundedChunkCount * 8, 1, MaximumQuestions);
    }

    internal static string QuestionDedupKey(QuestionDraft draft)
    {
        var point = draft.KnowledgePointId?.ToString("D")
            ?? $"unbound:{draft.SourceReferences.FirstOrDefault()?.MaterialId:D}:{draft.SourceReferences.FirstOrDefault()?.StartOffset}";
        var atom = string.Join('|', draft.CorrectAnswers.Select(QuestionTextParser.Normalize).OrderBy(value => value, StringComparer.Ordinal));
        return $"{point}:{draft.Kind}:{atom}";
    }
}
