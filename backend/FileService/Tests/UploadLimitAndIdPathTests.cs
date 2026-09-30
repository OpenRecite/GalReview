using Xunit;

namespace FileService.Tests;

/// <summary>
/// 上传限制与 ID/路径解析边界：10 MiB 上限契约值、displayName/subjectCode 格式、
/// 含 charset 的 Content-Type 解析、路径段隔离。
/// </summary>
public sealed class UploadLimitAndIdPathTests
{
    // 与 Program.cs 的 MaxFileSizeBytes / 契约 §5.4「单文件上传上限为 10 MiB」保持一致。
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;

    [Fact]
    public void Documented_max_upload_is_ten_mebibytes()
    {
        Assert.Equal(10_485_760, MaxFileSizeBytes);
    }

    [Theory]
    [InlineData("notes.txt", "text/plain; charset=utf-8", ParserInputKind.PlainText)]
    [InlineData("readme.md", "text/markdown; charset=utf-8", ParserInputKind.Markdown)]
    [InlineData("page.html", "text/html; charset=gbk", ParserInputKind.Html)]
    [InlineData("report.pdf", "application/pdf", ParserInputKind.Pdf)]
    [InlineData("report.pdf", "application/pdf; charset=binary", ParserInputKind.Pdf)]
    [InlineData("scan.jpg", "image/jpeg", ParserInputKind.Image)]
    [InlineData("slides.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", ParserInputKind.Pptx)]
    [InlineData("mail.mhtml", "multipart/related", ParserInputKind.Mhtml)]
    public void Content_type_with_parameters_still_resolves_parser_kind(
        string fileName,
        string mediaType,
        ParserInputKind expected)
    {
        Assert.Equal(expected, ParserInputPolicy.ResolveParserKind(fileName, mediaType));
    }

    [Fact]
    public void Mimetype_wins_over_extension_for_binary_document_types()
    {
        // 契约 §5：application/pdf 即使扩展名为 .bin 也必须走 PDF 解析。
        Assert.Equal(ParserInputKind.Pdf, ParserInputPolicy.ResolveParserKind("blob.bin", "application/pdf"));
        Assert.Equal(ParserInputKind.Docx, ParserInputPolicy.ResolveParserKind("blob.bin",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
    }

    [Theory]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("NOTES.TXT", "TEXT/PLAIN")]
    [InlineData("Readme.Md", "Application/Octet-Stream")]
    public void Support_checks_are_case_insensitive(string fileName, string mediaType)
    {
        Assert.True(ParserInputPolicy.IsSupported(fileName, mediaType));
    }

    [Fact]
    public void Null_or_blank_inputs_throw_argument_exceptions()
    {
        Assert.Throws<ArgumentNullException>(() => ParserInputPolicy.IsSupported(null!, "text/plain"));
        Assert.Throws<ArgumentException>(() => ParserInputPolicy.IsSupported("  ", "text/plain"));
        Assert.Throws<ArgumentException>(() => ParserInputPolicy.IsSupported("a.txt", " "));
    }

    [Fact]
    public void Material_id_is_a_guid_string_not_a_filesystem_path()
    {
        // 路径映射契约：materialId 是标识符，真正的存储路径/内容句柄由 store 决定
        // （LocalFileStore → contentRoot/{id}.bin；MongoFileStore → GridFS ObjectId）。
        var store = TestSupport.NewStore();
        var owner = Guid.NewGuid().ToString();
        var stored = store.CreateAsync(owner, TestSupport.File("../../evil.txt", "text/plain", "x"), "E", null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.True(Guid.TryParse(stored.Material.MaterialId, out _));
        // 原始文件名只保留文件名段，路径穿越段被剥离
        Assert.Equal("evil.txt", stored.Material.OriginalFileName);
        // 内容落盘路径固定在 content root 之下，不会随原始文件名逃逸
        var contentRoot = TestSupport.ContentRootOf(store);
        Assert.StartsWith(contentRoot, stored.ContentPath);
        Assert.Equal("evil.txt", Path.GetFileName(stored.Material.OriginalFileName));
    }

    [Fact]
    public void Subject_code_contract_shape_is_documented_for_gateway_validation()
    {
        // Program.cs NormalizeSubjectCode：^[A-Z][A-Z0-9_]{0,31}$（大写化后）
        var pattern = "^[A-Z][A-Z0-9_]{0,31}$";
        Assert.Matches(pattern, "AGRONOMY");
        Assert.Matches(pattern, "A");
        Assert.Matches(pattern, "BIO_CHEM2");
        Assert.DoesNotMatch(pattern, "agronomy"); // 未大写化
        Assert.DoesNotMatch(pattern, "1ABC");
        Assert.DoesNotMatch(pattern, "BIO-CHEM");
        Assert.DoesNotMatch(pattern, new string('A', 33));
    }
}
