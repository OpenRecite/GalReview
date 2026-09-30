using Xunit;

namespace FileService.Tests;

public sealed class ParserInputPolicyTests
{
    [Theory]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("slides.pdf", "application/pdf")]
    [InlineData("handout.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("deck.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation")]
    [InlineData("page.html", "text/html")]
    [InlineData("mail.mhtml", "multipart/related")]
    [InlineData("scan.jpg", "image/jpeg")]
    [InlineData("scan.png", "image/png")]
    [InlineData("blob.bin", "application/octet-stream")]
    public void Supported_inputs_are_accepted(string fileName, string mediaType)
    {
        Assert.True(ParserInputPolicy.IsSupported(fileName, mediaType));
    }

    [Theory]
    [InlineData("malware.exe", "application/x-msdownload")]
    [InlineData("archive.zip", "application/zip")]
    [InlineData("audio.mp3", "audio/mpeg")]
    public void Unsupported_inputs_are_rejected(string fileName, string mediaType)
    {
        Assert.False(ParserInputPolicy.IsSupported(fileName, mediaType));
    }

    [Fact]
    public void ResolveParserKind_maps_common_formats()
    {
        Assert.Equal(ParserInputKind.Pdf, ParserInputPolicy.ResolveParserKind("a.pdf", "application/pdf"));
        Assert.Equal(ParserInputKind.Docx, ParserInputPolicy.ResolveParserKind("a.docx", "application/octet-stream"));
        Assert.Equal(ParserInputKind.Markdown, ParserInputPolicy.ResolveParserKind("a.md", "text/markdown"));
        Assert.Equal(ParserInputKind.PlainText, ParserInputPolicy.ResolveParserKind("a.txt", "text/plain"));
        Assert.Equal(ParserInputKind.Image, ParserInputPolicy.ResolveParserKind("a.png", "image/png"));
    }

    [Fact]
    public void ResolveParserKind_throws_for_unsupported()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ParserInputPolicy.ResolveParserKind("a.exe", "application/x-msdownload"));
    }
}
