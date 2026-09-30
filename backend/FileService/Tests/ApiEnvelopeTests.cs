using System.Text.Json;
using Xunit;

namespace FileService.Tests;

/// <summary>
/// 统一错误/成功信封契约（docs/contract.md §2.3–2.4）：
/// ApiFailure.data 恒为 null，error 为 ApiError(code/message/details)，traceId 回传。
/// </summary>
public sealed class ApiEnvelopeTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ApiFailure_Create_builds_documented_error_envelope()
    {
        var failure = ApiFailure.Create("FILE_TOO_LARGE", "The file exceeds the 10 MB limit.", "trace-1");

        Assert.Null(failure.Data);
        Assert.Equal("FILE_TOO_LARGE", failure.Error.Code);
        Assert.Equal("The file exceeds the 10 MB limit.", failure.Error.Message);
        Assert.NotNull(failure.Error.Details);
        Assert.Equal("trace-1", failure.TraceId);
    }

    [Fact]
    public void ApiSuccess_Create_fills_empty_meta_and_keeps_trace()
    {
        var success = ApiSuccess.Create(new { status = "live" }, "trace-2");

        Assert.NotNull(success.Data);
        Assert.NotNull(success.Meta);
        Assert.Equal("trace-2", success.TraceId);
    }

    [Fact]
    public void ApiFailure_serializes_with_null_data_and_error_object()
    {
        var failure = ApiFailure.Create("MATERIAL_TEXT_EXTRACTION_FAILED", "Text extraction failed.", "trace-3");
        var json = JsonSerializer.Serialize(failure, WebJson);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("data").ValueKind);
        var error = root.GetProperty("error");
        Assert.Equal("MATERIAL_TEXT_EXTRACTION_FAILED", error.GetProperty("code").GetString());
        Assert.Equal("Text extraction failed.", error.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Object, error.GetProperty("details").ValueKind);
        Assert.Equal("trace-3", root.GetProperty("traceId").GetString());
    }

    [Theory]
    [InlineData("VALIDATION_ERROR")]
    [InlineData("AUTH_REQUIRED")]
    [InlineData("FORBIDDEN")]
    [InlineData("RESOURCE_NOT_FOUND")]
    [InlineData("STATE_CONFLICT")]
    [InlineData("FILE_TOO_LARGE")]
    [InlineData("MEDIA_TYPE_UNSUPPORTED")]
    [InlineData("MATERIAL_TEXT_NOT_READY")]
    [InlineData("MATERIAL_TEXT_EXTRACTION_FAILED")]
    [InlineData("SERVICE_UNAVAILABLE")]
    [InlineData("INTERNAL_ERROR")]
    public void Contract_error_codes_are_string_stable(string code)
    {
        var failure = ApiFailure.Create(code, "message", "t");
        Assert.Equal(code, failure.Error.Code);
    }

    [Fact]
    public void ApiError_carries_custom_details_payload()
    {
        var error = new ApiError("CREDITS_INSUFFICIENT", "credits 不足", new { required = 1.5m });
        Assert.Equal("CREDITS_INSUFFICIENT", error.Code);
        Assert.NotNull(error.Details);
    }
}
