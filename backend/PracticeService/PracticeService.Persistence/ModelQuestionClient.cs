using Microsoft.Extensions.Configuration;
using PracticeService.Application;
using PracticeService.Domain;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PracticeService.Persistence;

internal sealed class ModelQuestionClient(IHttpClientFactory clients, IConfiguration configuration)
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private string? ApiKey => configuration["QuestionGeneration:ApiKey"];
    private string Endpoint => configuration["QuestionGeneration:Endpoint"] ?? "https://api.deepseek.com/chat/completions";
    private string Model => configuration["QuestionGeneration:Model"] ?? "deepseek-v4-flash";

    internal bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    internal async Task<GeneratedChunk> GenerateChunkAsync(SourceChunk chunk, QuestionGenerationInput input, CancellationToken cancellationToken)
    {
        var allowedKinds = (input.Kinds.Count == 0 ? ReciteQuestionGenerator.ClassicKinds : input.Kinds).Select(kind => kind.ToString()).ToArray();
        var points = chunk.Points.Select(point => new { pointId = point.KnowledgePointId, point.Title, point.Summary, point.Tags }).ToArray();
        var system = "你是农学、社会科学与人文类复习资料的题库编辑。只处理事实、概念、关系、比较、步骤和论述，不生成计算题、公式推导或复杂理工题。先从原文识别可独立作答的答案原子，再据此写题。不得补充原文没有的信息。输出严格 JSON。";
        var user = JsonSerializer.Serialize(new
        {
            task = "从 evidence 中生成至多 8 道互不重复的复习题",
            rules = new[]
            {
                "每题必须绑定 suppliedPoints 中唯一 pointId",
                "sourceQuote 必须逐字复制 evidence 中能完整支持标准答案的最短连续原文",
                "correctAnswers 必须逐字出现在 sourceQuote 中；简答题可把多个原文要点作为多个数组元素",
                "填空题必须有 ____ 且答案为不超过 40 字的明确短语",
                "名词解释只用于原文存在术语与定义关系时",
                "单选题必须恰有 A-D 四项，只有正确项文本可由 sourceQuote 支持；无法构造可靠干扰项就不要出单选",
                "题干不得包含答案，不得使用请概括下述内容之类把答案原句塞进题干的模板",
                "不要为凑题数重复同一知识原子"
            },
            allowedKinds,
            suppliedPoints = points,
            evidence = chunk.Text,
            output = new
            {
                questions = new[] { new { kind = "FillBlank|TermDefinition|Essay|SingleChoice", prompt = "", options = new[] { new { id = "A", text = "" } }, correctAnswers = new[] { "" }, explanation = "", score = 2, difficulty = 2, knowledgePointId = Guid.Empty, sourceQuote = "" } }
            }
        }, _json);
        var generated = await CompleteJsonAsync(system, user, cancellationToken);
        var diagnostics = new List<PracticeJobDiagnostic>();
        IReadOnlyList<ModelCandidate> candidates;
        try
        {
            candidates = ParseModelCandidates(generated.Content, chunk, input);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            diagnostics.Add(new(chunk.Material.MaterialId, "QUESTION_MODEL_OUTPUT_INVALID",
                "题目模型返回的 JSON 不符合候选题契约，未保存任何猜测题目。", false));
            return new([], diagnostics, generated.TokenUnits);
        }
        if (candidates.Count == 0)
        {
            diagnostics.Add(new(chunk.Material.MaterialId, "NO_VERIFIED_QUESTIONS_IN_CHUNK",
                $"资料区间 {chunk.StartOffset}-{chunk.StartOffset + chunk.Text.Length} 没有题目通过确定性校验。", false));
            return new([], diagnostics, generated.TokenUnits);
        }

        var verifierUser = JsonSerializer.Serialize(new
        {
            task = "在与生成调用分离的第二次推理中复核候选题。只能依据 evidence 重新作答，再将 recoveredAnswers 与候选标准答案比较；仅接受答案一致、题干清晰且 pointId 语义一致的题。",
            evidence = chunk.Text,
            suppliedPoints = points,
            candidates = candidates.Select((candidate, index) => new { index, candidate.Draft.Kind, candidate.Draft.Prompt, candidate.Draft.Options, candidate.Draft.CorrectAnswers, candidate.PointId, candidate.SourceQuote }),
            output = new { results = new[] { new { index = 0, accepted = true, recoveredAnswers = new[] { "" }, reason = "" } } }
        }, _json);
        var verified = await CompleteJsonAsync("你是第二遍来源约束题目核验器。不得利用 evidence 之外的常识放宽答案。输出严格 JSON。", verifierUser, cancellationToken);
        IReadOnlySet<int> accepted;
        try
        {
            accepted = ParseRoundTripResults(verified.Content, candidates);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            diagnostics.Add(new(chunk.Material.MaterialId, "QUESTION_ROUND_TRIP_OUTPUT_INVALID",
                "第二遍回验返回的 JSON 不符合回验契约，候选题均未进入 READY。", false));
            return new([], diagnostics, checked(generated.TokenUnits + verified.TokenUnits));
        }
        var result = candidates.Where((_, index) => accepted.Contains(index)).Select(candidate => candidate.Draft).ToArray();
        if (result.Length < candidates.Count)
            diagnostics.Add(new(chunk.Material.MaterialId, "QUESTION_ROUND_TRIP_REJECTED",
                $"第二遍回验拒绝了 {candidates.Count - result.Length} 道不能从同一证据稳定还原答案的候选题。", false));
        return new(result, diagnostics, checked(generated.TokenUnits + verified.TokenUnits));
    }

    private async Task<ModelCompletion> CompleteJsonAsync(string system, string user, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        request.Content = JsonContent.Create(new
        {
            model = Model,
            messages = new[] { new { role = "system", content = system }, new { role = "user", content = user } },
            response_format = new { type = "json_object" },
            temperature = 0.1,
            max_tokens = 6000
        }, options: _json);
        using var response = await clients.CreateClient("question-generation").SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new PracticeDomainException((int)response.StatusCode, "QUESTION_MODEL_FAILED", $"题目模型调用失败：HTTP {(int)response.StatusCode}。");
        JsonDocument document;
        try { document = JsonDocument.Parse(body); }
        catch (JsonException)
        {
            throw new PracticeDomainException(502, "QUESTION_MODEL_RESPONSE_INVALID", "题目模型返回了非 JSON 响应，未保存候选题。");
        }
        using (document)
        {
            if (!document.RootElement.TryGetProperty("usage", out var usage)
                || !usage.TryGetProperty("total_tokens", out var total)
                || !total.TryGetInt64(out var tokens)
                || tokens <= 0)
                throw new PracticeDomainException(502, "QUESTION_MODEL_USAGE_MISSING", "题目模型未返回有效 usage.total_tokens，无法按实际消耗结算 credits。");
            if (!document.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var rawContent)
                || rawContent.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(rawContent.GetString()))
                throw new PracticeDomainException(502, "QUESTION_MODEL_RESPONSE_INVALID", "题目模型响应缺少 choices[0].message.content。");
            return new(rawContent.GetString()!, tokens);
        }
    }

    private static IReadOnlyList<ModelCandidate> ParseModelCandidates(string json, SourceChunk chunk, QuestionGenerationInput input)
    {
        var candidates = new List<ModelCandidate>();
        using var document = JsonDocument.Parse(ExtractJsonObject(json));
        if (!document.RootElement.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array) return candidates;
        var allowedKinds = (input.Kinds.Count == 0 ? ReciteQuestionGenerator.ClassicKinds : input.Kinds).ToHashSet();
        var pointIds = chunk.Points.Select(point => point.KnowledgePointId).ToHashSet();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in questions.EnumerateArray())
        {
            try
            {
                if (!Enum.TryParse<PracticeQuestionKind>(item.GetProperty("kind").GetString(), true, out var kind) || !allowedKinds.Contains(kind)) continue;
                var prompt = item.GetProperty("prompt").GetString()?.Trim() ?? string.Empty;
                var quote = item.GetProperty("sourceQuote").GetString()?.Trim() ?? string.Empty;
                if (prompt.Length < 2 || quote.Length < 2 || QuestionTextParser.Normalize(prompt) == QuestionTextParser.Normalize(quote) || !seen.Add(QuestionTextParser.Normalize(prompt))) continue;
                var relativeOffset = chunk.Text.IndexOf(quote, StringComparison.Ordinal);
                if (relativeOffset < 0) continue;
                var pointId = item.GetProperty("knowledgePointId").GetGuid();
                if (!pointIds.Contains(pointId)) continue;
                var answers = item.GetProperty("correctAnswers").EnumerateArray().Select(answer => answer.GetString()?.Trim() ?? string.Empty).Where(answer => answer.Length > 0).ToArray();
                if (answers.Length == 0 || QuestionTextParser.ContainsPageNoise(quote)) continue;
                var options = item.TryGetProperty("options", out var rawOptions) && rawOptions.ValueKind == JsonValueKind.Array
                    ? rawOptions.EnumerateArray().Select(option => new QuestionOption(option.GetProperty("id").GetString() ?? string.Empty, option.GetProperty("text").GetString() ?? string.Empty)).ToArray()
                    : [];
                if (kind != PracticeQuestionKind.SingleChoice
                    && answers.Any(answer => !QuestionTextParser.Normalize(quote).Contains(QuestionTextParser.Normalize(answer), StringComparison.Ordinal))) continue;
                if (!ValidateGeneratedShape(kind, prompt, quote, options, answers)) continue;
                var source = new SourceReference(chunk.Material.MaterialId, chunk.StartOffset + relativeOffset,
                    chunk.StartOffset + relativeOffset + quote.Length, chunk.Material.SourceMapVersion, PracticeRules.Sha256(quote));
                var score = item.TryGetProperty("score", out var rawScore) && rawScore.TryGetDecimal(out var parsedScore) ? Math.Clamp(parsedScore, 1, 20) : QuestionTextParser.DefaultScore(kind);
                var difficulty = item.TryGetProperty("difficulty", out var rawDifficulty) && rawDifficulty.TryGetInt32(out var parsedDifficulty) ? Math.Clamp(parsedDifficulty, 1, 5) : QuestionTextParser.InferDifficulty(kind);
                var explanation = item.TryGetProperty("explanation", out var rawExplanation) ? rawExplanation.GetString() : null;
                var draft = new QuestionDraft(kind, prompt, options, answers, explanation, score, difficulty, pointId, [source], QuestionStatus.Ready);
                PracticeRules.ValidateAnswerShape(kind, options, answers);
                candidates.Add(new(draft, pointId, quote));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _ = error;
            }
        }
        return candidates;
    }

    private static bool ValidateGeneratedShape(PracticeQuestionKind kind, string prompt, string quote,
        IReadOnlyList<QuestionOption> options, IReadOnlyList<string> answers)
    {
        if (kind != PracticeQuestionKind.SingleChoice
            && answers.Any(answer => QuestionTextParser.Normalize(prompt).Contains(QuestionTextParser.Normalize(answer), StringComparison.Ordinal))) return false;
        if (kind == PracticeQuestionKind.FillBlank) return Regex.Matches(prompt, "____").Count == answers.Count
            && answers.All(answer => answer.Length <= 40) && options.Count == 0
            && prompt.Replace("____", "待填内容", StringComparison.Ordinal).Length >= 6;
        if (kind == PracticeQuestionKind.TermDefinition) return options.Count == 0 && !QuestionTextParser.Normalize(prompt).Contains(QuestionTextParser.Normalize(quote), StringComparison.Ordinal);
        if (kind == PracticeQuestionKind.Essay) return options.Count == 0 && !QuestionTextParser.Normalize(prompt).Contains(QuestionTextParser.Normalize(quote), StringComparison.Ordinal);
        if (kind != PracticeQuestionKind.SingleChoice || options.Count != 4 || answers.Count != 1) return false;
        var answerOption = options.SingleOrDefault(option => string.Equals(option.Id, answers[0], StringComparison.OrdinalIgnoreCase));
        if (answerOption is null
            || QuestionTextParser.Normalize(prompt).Contains(QuestionTextParser.Normalize(answerOption.Text), StringComparison.Ordinal)
            || !QuestionTextParser.Normalize(quote).Contains(QuestionTextParser.Normalize(answerOption.Text), StringComparison.Ordinal)) return false;
        return options.Where(option => !string.Equals(option.Id, answerOption.Id, StringComparison.OrdinalIgnoreCase))
            .All(option => !QuestionTextParser.Normalize(quote).Contains(QuestionTextParser.Normalize(option.Text), StringComparison.Ordinal));
    }

    private static IReadOnlySet<int> ParseRoundTripResults(string json, IReadOnlyList<ModelCandidate> candidates)
    {
        using var document = JsonDocument.Parse(ExtractJsonObject(json));
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return new HashSet<int>();
        var accepted = new HashSet<int>();
        foreach (var result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("index", out var rawIndex) || !rawIndex.TryGetInt32(out var index)
                || index < 0 || index >= candidates.Count
                || !result.TryGetProperty("accepted", out var rawAccepted) || rawAccepted.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !rawAccepted.GetBoolean()
                || !result.TryGetProperty("recoveredAnswers", out var recovered) || recovered.ValueKind != JsonValueKind.Array) continue;
            var recoveredAnswers = recovered.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()?.Trim() ?? string.Empty)
                .Where(value => value.Length > 0)
                .Select(QuestionTextParser.Normalize)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var expectedAnswers = candidates[index].Draft.CorrectAnswers
                .Select(QuestionTextParser.Normalize)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (recoveredAnswers.SequenceEqual(expectedAnswers, StringComparer.Ordinal)) accepted.Add(index);
        }
        return accepted;
    }

    private static string ExtractJsonObject(string value)
    {
        var start = value.IndexOf('{');
        var end = value.LastIndexOf('}');
        return start >= 0 && end > start ? value[start..(end + 1)] : "{}";
    }
}

internal sealed record ModelCompletion(string Content, long TokenUnits);
internal sealed record ModelCandidate(QuestionDraft Draft, Guid PointId, string SourceQuote);
internal sealed record GeneratedChunk(IReadOnlyList<QuestionDraft> Drafts, IReadOnlyList<PracticeJobDiagnostic> Diagnostics, long TokenUnits);
