using Xunit;

namespace FileService.Tests;

/// <summary>纯文本交付基线校验器（docs/contract.md §5.2.1）。</summary>
public sealed class FileTextContractValidatorTests
{
    private static ExtractionResult Build(
        string text,
        IReadOnlyList<TextSourceSpan>? sourceMap = null,
        IReadOnlyList<TextDocumentBlock>? blocks = null)
    {
        var map = sourceMap ?? (text.Length == 0
            ? Array.Empty<TextSourceSpan>()
            : new TextSourceSpan[] { new(0, text.Length, null, null, null) });
        var body = blocks ?? (text.Length == 0
            ? Array.Empty<TextDocumentBlock>()
            : new TextDocumentBlock[] { new("PARAGRAPH", null, text, map[0]) });
        return new ExtractionResult(text, map, body);
    }

    [Fact]
    public void Valid_single_block_document_passes()
    {
        FileTextContractValidator.Validate(Build("hello world"));
    }

    [Fact]
    public void Valid_multi_block_document_with_ordered_spans_passes()
    {
        const string text = "para one\npara two";
        var first = new TextSourceSpan(0, 8, 1, 0, "Paragraph");
        var second = new TextSourceSpan(9, 17, 2, 1, "Paragraph");
        var document = Build(text,
            new[] { first, second },
            new[]
            {
                new TextDocumentBlock("PARAGRAPH", null, "para one", first),
                new TextDocumentBlock("PARAGRAPH", null, "para two", second),
            });
        FileTextContractValidator.Validate(document);
    }

    [Fact]
    public void Empty_text_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            FileTextContractValidator.Validate(Build("")));
    }

    [Fact]
    public void Whitespace_only_text_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            FileTextContractValidator.Validate(Build("   \n\t")));
    }

    [Fact]
    public void Empty_source_map_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            FileTextContractValidator.Validate(new ExtractionResult("text", [], Build("text").Blocks)));
    }

    [Fact]
    public void Empty_blocks_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            FileTextContractValidator.Validate(new ExtractionResult("text", Build("text").SourceMap, [])));
    }

    [Fact]
    public void Overlapping_source_map_spans_are_rejected()
    {
        const string text = "abcdef";
        var a = new TextSourceSpan(0, 4, null, null, null);
        var b = new TextSourceSpan(3, 6, null, null, null);
        Assert.Throws<InvalidOperationException>(() =>
            FileTextContractValidator.Validate(Build(text, new[] { a, b },
                new[]
                {
                    new TextDocumentBlock("PARAGRAPH", null, "abcd", a),
                    new TextDocumentBlock("PARAGRAPH", null, "def", b),
                })));
    }

    [Fact]
    public void Source_span_end_beyond_text_is_rejected()
    {
        const string text = "abc";
        var span = new TextSourceSpan(0, 10, null, null, null);
        Assert.Throws<InvalidOperationException>(() =>
            FileTextContractValidator.Validate(Build(text, new[] { span },
                new[] { new TextDocumentBlock("PARAGRAPH", null, "abc", span) })));
    }

    [Fact]
    public void Zero_length_span_is_rejected()
    {
        const string text = "abc";
        var span = new TextSourceSpan(1, 1, null, null, null);
        Assert.Throws<InvalidOperationException>(() =>
            FileTextContractValidator.Validate(Build(text, new[] { span },
                new[] { new TextDocumentBlock("PARAGRAPH", null, "abc", span) })));
    }

    [Fact]
    public void Block_text_must_match_source_slice_exactly()
    {
        const string text = "hello world";
        var span = new TextSourceSpan(0, 5, null, null, null);
        Assert.Throws<InvalidOperationException>(() =>
            FileTextContractValidator.Validate(Build(text, new[] { span },
                new[] { new TextDocumentBlock("PARAGRAPH", null, "HELLO", span) })));
    }

    [Fact]
    public void Null_extraction_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            FileTextContractValidator.Validate(null!));
    }
}
