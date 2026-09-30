using PracticeService.Domain;
using System.Text.RegularExpressions;

namespace PracticeService.Persistence;

internal static class QuestionTextParser
{
    private static readonly Regex OptionMarker = new(
        @"(?<![A-Za-z0-9])(?<id>[A-DＡ-Ｄ])[\.、．]\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PageNoise = new(
        @"(?:山东农业大学农学院农业生态学教学组版权所有不得复制！\s*\d{1,3}|2019级植科一班张旋波|农业生态学试题库\s*2021版)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static PracticeQuestionKind InferKind(string? section, string prompt, int optionCount)
    {
        if (optionCount >= 2 || section is "选择题" or "单项选择题") return PracticeQuestionKind.SingleChoice;
        if (section == "填空题" || prompt.Contains("____", StringComparison.Ordinal) || prompt.Contains("（ ）", StringComparison.Ordinal)) return PracticeQuestionKind.FillBlank;
        if (section == "名词解释") return PracticeQuestionKind.TermDefinition;
        return PracticeQuestionKind.Essay;
    }

    internal static string EnsureQuestionPrompt(string prompt, PracticeQuestionKind kind)
    {
        prompt = Regex.Replace(prompt.Trim(), @"^[\d\s\.、]+", string.Empty).Trim();
        prompt = Regex.Replace(prompt, @"[（(]?1[）)]?\s*定义$", string.Empty).Trim();
        if (kind == PracticeQuestionKind.TermDefinition)
        {
            var term = prompt.TrimEnd('。', '？', '?', '：', ':');
            return term.StartsWith("解释", StringComparison.Ordinal) || term.StartsWith("什么是", StringComparison.Ordinal)
                ? term + "。"
                : $"请解释“{term}”。";
        }
        if (prompt.EndsWith('？') || prompt.EndsWith('?') || prompt.EndsWith('。')) return prompt;
        return prompt + "。";
    }

    internal static string CleanAnswer(string value)
    {
        var answer = CleanText(value);
        answer = Regex.Replace(answer, @"(?ms)\s*评分标准.*$", string.Empty);
        answer = Regex.Replace(answer, @"[ \t]+", " ");
        answer = Regex.Replace(answer, @"\s*\r?\n\s*", "\n");
        return answer.Trim();
    }

    internal static string CleanText(string value)
    {
        var cleaned = PageNoise.Replace(value, " ");
        cleaned = Regex.Replace(cleaned, @"[ \t]+", " ");
        return cleaned.Trim();
    }

    internal static QuestionOption[] ParseOptions(string body, out string prompt)
    {
        var matches = OptionMarker.Matches(body).Cast<Match>().ToArray();
        if (matches.Length < 2)
        {
            prompt = body.Trim();
            return [];
        }
        prompt = body[..matches[0].Index].Trim();
        return matches.Select((match, index) =>
        {
            var end = index + 1 < matches.Length ? matches[index + 1].Index : body.Length;
            return new QuestionOption(NormalizeOptionId(match.Groups["id"].Value), body[(match.Index + match.Length)..end].Trim());
        }).Where(option => option.Text.Length > 0).ToArray();
    }

    internal static bool EvidenceSupportsAnswer(string evidence, string answer) =>
        Normalize(CleanText(evidence)).Contains(Normalize(CleanText(answer)), StringComparison.Ordinal);

    internal static bool ContainsPageNoise(string value) => PageNoise.IsMatch(value);

    internal static decimal ParseScore(string? sectionMeta, string raw, PracticeQuestionKind kind)
    {
        var value = $"{sectionMeta} {raw[..Math.Min(raw.Length, 80)]}";
        var match = Regex.Match(value, @"(?<score>\d{1,2})\s*分");
        return match.Success && decimal.TryParse(match.Groups["score"].Value, out var score) && score is > 0 and <= 20
            ? score : DefaultScore(kind);
    }

    internal static decimal DefaultScore(PracticeQuestionKind kind) => kind switch
    {
        PracticeQuestionKind.SingleChoice => 3,
        PracticeQuestionKind.FillBlank => 2,
        PracticeQuestionKind.TermDefinition => 4,
        _ => 5
    };

    internal static int InferDifficulty(PracticeQuestionKind kind) => kind switch
    {
        PracticeQuestionKind.FillBlank => 2,
        PracticeQuestionKind.SingleChoice => 2,
        _ => 3
    };

    internal static string Normalize(string value) => new(value.Normalize(System.Text.NormalizationForm.FormC)
        .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    internal static string NormalizeOptionId(string value)
    {
        var character = value.Trim().ToUpperInvariant()[0];
        if (character is >= 'Ａ' and <= 'Ｈ') character = (char)('A' + character - 'Ａ');
        return character.ToString();
    }

    internal static string TrimForDiagnostic(string value) => value.Length <= 32 ? value : value[..32] + "…";
}
