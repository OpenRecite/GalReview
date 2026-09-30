using System.Text.Json;
using Xunit;

namespace FileService.Tests;

/// <summary>
/// FileService ↔ OCRService 响应契约：OcrResponse/OcrPage 形状必须与
/// OCRService `POST /v1/ocr` 返回的 `{pages:[{pageNumber,lines,...}]}` 兼容。
/// </summary>
public sealed class OcrResponseContractTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Ocr_response_deserializes_pages_with_page_number_and_lines()
    {
        const string body = """
            {
              "pages": [
                { "pageNumber": 1, "lines": ["Hello", "World"], "formulas": [] },
                { "pageNumber": 2, "lines": ["第二行"], "formulas": ["$$E=mc^2$$"] }
              ]
            }
            """;

        var result = JsonSerializer.Deserialize<OcrResponse>(body, Options);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Pages.Count);
        Assert.Equal(1, result.Pages[0].PageNumber);
        Assert.Equal(new[] { "Hello", "World" }, result.Pages[0].Lines);
        Assert.Equal(2, result.Pages[1].PageNumber);
        Assert.Equal(new[] { "第二行" }, result.Pages[1].Lines);
    }

    [Fact]
    public void Ocr_response_accepts_case_insensitive_property_names()
    {
        // FileService 使用 PropertyNameCaseInsensitive = true 反序列化 OCR 响应。
        const string body = """{ "pages": [ { "pagenumber": 3, "lines": ["x"] } ] }""";
        var result = JsonSerializer.Deserialize<OcrResponse>(
            body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(result);
        Assert.Equal(3, result!.Pages[0].PageNumber);
        Assert.Equal(new[] { "x" }, result.Pages[0].Lines);
    }

    [Fact]
    public void Empty_pages_array_is_a_valid_response_shape()
    {
        var result = JsonSerializer.Deserialize<OcrResponse>("""{ "pages": [] }""", Options);
        Assert.NotNull(result);
        Assert.Empty(result!.Pages);
    }

    [Fact]
    public void Ocr_progress_shape_matches_polling_contract()
    {
        const string body = """{ "status": "RUNNING", "currentPage": 2, "totalPages": 5, "phase": "RECOGNIZING" }""";
        var progress = JsonSerializer.Deserialize<OcrProgress>(body, Options);

        Assert.Equal("RUNNING", progress!.Status);
        Assert.Equal(2, progress.CurrentPage);
        Assert.Equal(5, progress.TotalPages);
        Assert.Equal("RECOGNIZING", progress.Phase);
    }

    [Fact]
    public void Extraction_result_tracks_ocr_used_flag()
    {
        var plain = new ExtractionResult("text", [], []);
        Assert.False(plain.OcrUsed);

        var fromOcr = new ExtractionResult("text", [], [], OcrUsed: true);
        Assert.True(fromOcr.OcrUsed);
    }
}
