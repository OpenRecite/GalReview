using PracticeService.Domain;

namespace PracticeService.Persistence;

internal static class QuestionChunker
{
    private const int ChunkCharacters = 1400;

    internal static IEnumerable<SourceChunk> BuildChunks(IReadOnlyList<MaterialText> materials, IReadOnlyList<PlanGraphPoint> points)
    {
        foreach (var material in materials)
        {
            var position = 0;
            while (position < material.Text.Length)
            {
                while (position < material.Text.Length && char.IsWhiteSpace(material.Text[position])) position++;
                if (position >= material.Text.Length) break;
                var start = position;
                var preferredEnd = Math.Min(start + ChunkCharacters, material.Text.Length);
                var end = FindChunkBoundary(material.Text, start, preferredEnd);
                while (end > start && char.IsWhiteSpace(material.Text[end - 1])) end--;
                if (end <= start)
                {
                    end = preferredEnd;
                }
                var text = material.Text[start..end].Trim();
                var actualStart = material.Text.IndexOf(text, start, StringComparison.Ordinal);
                if (text.Length >= 40)
                    yield return new(material, actualStart, text, BindCandidatePoints(text, points));
                position = end;
            }
        }
    }

    internal static bool IsCoveredChunk(SourceChunk chunk, IReadOnlyList<GroundedRange> ranges)
    {
        if (chunk.Text.Length == 0) return true;
        var chunkEnd = chunk.StartOffset + chunk.Text.Length;
        var covered = ranges.Where(range => range.MaterialId == chunk.Material.MaterialId)
            .Sum(range => Math.Max(0, Math.Min(chunkEnd, range.EndOffset) - Math.Max(chunk.StartOffset, range.StartOffset)));
        return covered >= chunk.Text.Length * 0.75;
    }

    private static int FindChunkBoundary(string text, int start, int preferredEnd)
    {
        if (preferredEnd >= text.Length) return text.Length;
        var minimum = start + Math.Max(1, (preferredEnd - start) * 3 / 5);
        for (var index = preferredEnd; index > minimum; index--)
        {
            if (text[index - 1] is '\n' or '。' or '！' or '？' or '.' or '!' or '?' or '；' or ';')
                return index;
        }
        return preferredEnd;
    }

    private static IReadOnlyList<PlanGraphPoint> BindCandidatePoints(string text, IReadOnlyList<PlanGraphPoint> points)
    {
        var normalized = QuestionTextParser.Normalize(text);
        var matched = points.Where(point =>
                QuestionTextParser.Normalize(point.Title).Length >= 2 && normalized.Contains(QuestionTextParser.Normalize(point.Title), StringComparison.Ordinal)
                || point.Tags.Any(tag => QuestionTextParser.Normalize(tag).Length >= 3 && normalized.Contains(QuestionTextParser.Normalize(tag), StringComparison.Ordinal)))
            .Take(24).ToArray();
        return matched;
    }
}

internal sealed record SourceChunk(MaterialText Material, int StartOffset, string Text, IReadOnlyList<PlanGraphPoint> Points);
