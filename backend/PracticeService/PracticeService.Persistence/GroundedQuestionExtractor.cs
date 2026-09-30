using PracticeService.Application;
using PracticeService.Domain;
using System.Text.RegularExpressions;

namespace PracticeService.Persistence;

internal static class GroundedQuestionExtractor
{
    private static readonly Regex ReferenceAnswerMarker = new(@"【参考答案】", RegexOptions.Compiled);
    private static readonly Regex NumberedItemMarker = new(
        @"(?<![\p{L}\p{N}])(?<number>\d{1,3})[\.、．]\s*",
        RegexOptions.Compiled);
    private static readonly Regex SectionHeading = new(
        @"(?<title>名词解释|填空题|选择题|单项选择题|简答题|论述题|综合题|大题|重要知识点|还有重要的知识点|结课思考题)",
        RegexOptions.Compiled);
    private static readonly Regex StructuralBoundary = new(
        @"(?:第[一二三四五六七八九十百\d]+章\s*[^\r\n]{0,80}|[一二三四五六七八九十]+、\s*(?:名词解释|填空题|选择题|单项选择题|简答题|论述题|综合题|大题|重要知识点|还有重要的知识点))",
        RegexOptions.Compiled);

    internal static ExtractionBatch ExtractGroundedQuestions(QuestionGenerationInput input)
    {
        var kinds = (input.Kinds.Count == 0 ? ReciteQuestionGenerator.ClassicKinds : input.Kinds).ToHashSet();
        var drafts = new List<QuestionDraft>();
        var diagnostics = new List<PracticeJobDiagnostic>();
        var coveredRanges = new List<GroundedRange>();
        var explicitItems = 0;
        foreach (var material in input.Materials)
        {
            var occupied = new List<(int Start, int End)>();
            var explicitSources = ExtractExplicitSourceItems(material.Text, diagnostics, material.MaterialId);
            explicitItems += explicitSources.Count;
            foreach (var sourceItem in explicitSources)
            {
                var parsed = ParseSourceItem(material, sourceItem, true, input.Points, diagnostics);
                occupied.Add((sourceItem.StartOffset, sourceItem.EndOffset));
                coveredRanges.Add(new(material.MaterialId, sourceItem.StartOffset, sourceItem.EndOffset));
                if (parsed is not null && kinds.Contains(parsed.Kind)) drafts.Add(parsed);
            }
            foreach (var sourceItem in ExtractSemiStructuredSourceItems(material.Text, occupied))
            {
                var parsed = ParseSourceItem(material, sourceItem, false, input.Points, diagnostics);
                coveredRanges.Add(new(material.MaterialId, sourceItem.StartOffset, sourceItem.EndOffset));
                if (parsed is not null && kinds.Contains(parsed.Kind)) drafts.Add(parsed);
            }
        }
        var unique = drafts
            .GroupBy(ReciteQuestionGenerator.QuestionDedupKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(draft => draft.SourceReferences[0].MaterialId)
            .ThenBy(draft => draft.SourceReferences[0].StartOffset)
            .ToArray();
        return new(unique, diagnostics, explicitItems >= 3, coveredRanges);
    }

    private static IReadOnlyList<SourceItem> ExtractExplicitSourceItems(
        string text,
        List<PracticeJobDiagnostic> diagnostics,
        Guid materialId)
    {
        var answerMarkers = ReferenceAnswerMarker.Matches(text).Cast<Match>().ToArray();
        if (answerMarkers.Length == 0) return [];
        var promptMarkers = new SourceMarker?[answerMarkers.Length];
        var previousAnswerMarkerEnd = 0;
        for (var index = 0; index < answerMarkers.Length; index++)
        {
            var answerMarker = answerMarkers[index];
            var candidates = NumberedItemMarker.Matches(text[previousAnswerMarkerEnd..answerMarker.Index]);
            if (candidates.Count > 0)
            {
                var candidate = candidates[^1];
                promptMarkers[index] = new(previousAnswerMarkerEnd + candidate.Index, candidate.Length);
            }
            else diagnostics.Add(new(materialId, "SOURCE_QUESTION_BOUNDARY_NOT_FOUND",
                $"资料中的第 {index + 1} 个参考答案标记之前没有可核对题号，已拒绝自动成题。", false));
            previousAnswerMarkerEnd = answerMarker.Index + answerMarker.Length;
        }

        var items = new List<SourceItem>();
        for (var index = 0; index < answerMarkers.Length; index++)
        {
            var promptMarker = promptMarkers[index];
            if (promptMarker is null) continue;
            var marker = answerMarkers[index];
            var structuralEnd = FindNextStructuralBoundary(text, marker.Index + marker.Length);
            var nextPromptEnd = index + 1 < promptMarkers.Length && promptMarkers[index + 1] is SourceMarker nextPrompt
                ? nextPrompt.Index
                : text.Length;
            var end = Math.Min(structuralEnd, nextPromptEnd);
            end = Math.Clamp(end, marker.Index + marker.Length, text.Length);
            var section = FindSection(text, promptMarker.Index);
            items.Add(new(promptMarker.Index, end,
                text[(promptMarker.Index + promptMarker.Length)..marker.Index],
                text[(marker.Index + marker.Length)..end], section?.Title, section?.Meta ?? string.Empty));
        }
        return items;
    }

    private static IReadOnlyList<SourceItem> ExtractSemiStructuredSourceItems(
        string text,
        IReadOnlyList<(int Start, int End)> occupied)
    {
        var sections = SectionHeading.Matches(text).Cast<Match>().ToArray();
        var items = new List<SourceItem>();
        foreach (var section in sections)
        {
            var title = section.Groups["title"].Value;
            var sectionStart = section.Index + section.Length;
            var nextSection = sections.FirstOrDefault(candidate => candidate.Index >= sectionStart);
            var boundary = StructuralBoundary.Match(text, sectionStart);
            var sectionEnd = new[]
                {
                    nextSection?.Index ?? text.Length,
                    boundary.Success ? boundary.Index : text.Length,
                    text.Length
                }
                .Min();
            if (sectionEnd <= sectionStart) continue;

            if (title == "结课思考题")
            {
                var raw = text[sectionStart..sectionEnd].TrimStart('：', ':', ' ', '\t');
                var answerStart = Regex.Match(raw, @"。(?=\s*\d{1,2}(?![\d\.]))");
                if (answerStart.Success)
                {
                    var absoluteStart = sectionStart + text[sectionStart..sectionEnd].IndexOf(raw, StringComparison.Ordinal);
                    var source = new SourceItem(absoluteStart, sectionEnd, raw[..(answerStart.Index + 1)],
                        raw[(answerStart.Index + 1)..], title, string.Empty);
                    if (!Overlaps(source.StartOffset, source.EndOffset, occupied)) items.Add(source);
                }
                continue;
            }

            var sectionText = text[sectionStart..sectionEnd];
            var markers = NumberedItemMarker.Matches(sectionText).Cast<Match>().ToArray();
            for (var index = 0; index < markers.Length; index++)
            {
                var marker = markers[index];
                var itemStart = sectionStart + marker.Index;
                var itemEnd = index + 1 < markers.Length ? sectionStart + markers[index + 1].Index : sectionEnd;
                if (Overlaps(itemStart, itemEnd, occupied)) continue;
                var body = text[(itemStart + marker.Length)..itemEnd];
                var split = FindAnswerSeparator(body, title);
                if (split < 1 || split >= body.Length - 1) continue;
                items.Add(new(itemStart, itemEnd, body[..split], body[(split + 1)..], title, string.Empty));
            }
        }
        return items;
    }

    private static int FindAnswerSeparator(string body, string section)
    {
        var colon = body.IndexOfAny(['：', ':']);
        return colon < 1 ? -1 : colon;
    }

    private static bool Overlaps(int start, int end, IReadOnlyList<(int Start, int End)> occupied) =>
        occupied.Any(range => start < range.End && end > range.Start);

    private static int FindNextStructuralBoundary(string text, int start)
    {
        var section = SectionHeading.Match(text, start);
        var structural = StructuralBoundary.Match(text, start);
        return new[]
        {
            section.Success ? section.Index : text.Length,
            structural.Success ? structural.Index : text.Length,
            text.Length
        }.Min();
    }

    private static (string Title, string Meta)? FindSection(string text, int offset)
    {
        var sections = SectionHeading.Matches(text[..Math.Clamp(offset, 0, text.Length)]);
        if (sections.Count == 0) return null;
        var match = sections[^1];
        return (match.Groups["title"].Value.Trim(), string.Empty);
    }

    private static QuestionDraft? ParseSourceItem(MaterialText material, SourceItem item, bool explicitAnswer,
        IReadOnlyList<PlanGraphPoint> points, List<PracticeJobDiagnostic> diagnostics)
    {
        var body = QuestionTextParser.CleanText(item.Prompt);
        var answer = QuestionTextParser.CleanAnswer(item.Answer);
        var options = QuestionTextParser.ParseOptions(body, out var prompt);
        prompt = Regex.Replace(prompt, @"^\s*\(?\d+\s*分\)?\s*", string.Empty).Trim();
        if (prompt.Length < 2 || answer.Length < 1) return null;

        var kind = QuestionTextParser.InferKind(item.SectionTitle, prompt, options.Length);
        IReadOnlyList<string> correctAnswers;
        if (kind == PracticeQuestionKind.SingleChoice)
        {
            var optionId = Regex.Match(answer, @"(?i)(?<![A-Z])[A-H](?![A-Z])").Value.ToUpperInvariant();
            if (options.Length < 2 || optionId.Length == 0 || !options.Any(option => option.Id == optionId))
            {
                diagnostics.Add(new(material.MaterialId, "SOURCE_CHOICE_ANSWER_AMBIGUOUS", $"原题“{QuestionTextParser.TrimForDiagnostic(prompt)}”的选项或答案无法唯一核对，保留为草稿。", false));
                return null;
            }
            else correctAnswers = [optionId];
        }
        else correctAnswers = [answer];

        var excerpt = material.Text[item.StartOffset..item.EndOffset];
        var source = new SourceReference(material.MaterialId, item.StartOffset, item.EndOffset,
            material.SourceMapVersion, PracticeRules.Sha256(excerpt));
        var binding = KnowledgePointBindingRules.Bind(kind, prompt, correctAnswers, excerpt, points, [source]);
        var answerSupported = QuestionTextParser.EvidenceSupportsAnswer(excerpt, answer);
        var reliableChoice = kind != PracticeQuestionKind.SingleChoice || options.Length == 4;
        var ready = binding.PointId.HasValue && answerSupported && reliableChoice;
        if (!binding.PointId.HasValue)
            diagnostics.Add(new(material.MaterialId, binding.Ambiguous ? "KNOWLEDGE_POINT_BINDING_AMBIGUOUS" : "KNOWLEDGE_POINT_SOURCE_NOT_FOUND",
                $"原题“{QuestionTextParser.TrimForDiagnostic(prompt)}”未能唯一绑定本册知识点，已保留为待核对草稿。", false));
        if (!answerSupported)
            diagnostics.Add(new(material.MaterialId, "SOURCE_ANSWER_NOT_SUPPORTED",
                $"原题“{QuestionTextParser.TrimForDiagnostic(prompt)}”的答案不能由同一原文区间完整支持，已保留为待核对草稿。", false));
        if (!reliableChoice)
            diagnostics.Add(new(material.MaterialId, "SOURCE_CHOICE_OPTIONS_UNRELIABLE",
                $"原题“{QuestionTextParser.TrimForDiagnostic(prompt)}”不是恰好四个选项，未自动收入 READY 题库。", false));
        return new(kind, QuestionTextParser.EnsureQuestionPrompt(prompt, kind), options, correctAnswers,
            explicitAnswer ? "题目与答案按资料原文提取。" : "题目与答案按资料中的可核对问答结构提取。",
            QuestionTextParser.ParseScore(item.SectionMeta, excerpt, kind), QuestionTextParser.InferDifficulty(kind), binding.PointId, [source], ready ? QuestionStatus.Ready : QuestionStatus.Draft);
    }
}

internal sealed record ExtractionBatch(IReadOnlyList<QuestionDraft> Drafts, IReadOnlyList<PracticeJobDiagnostic> Diagnostics,
    bool IsExplicitQuestionBank, IReadOnlyList<GroundedRange> CoveredRanges);
internal sealed record GroundedRange(Guid MaterialId, int StartOffset, int EndOffset);
internal sealed record SourceMarker(int Index, int Length);
internal sealed record SourceItem(int StartOffset, int EndOffset, string Prompt, string Answer, string? SectionTitle, string SectionMeta);
