using System.Reflection;
using MongoDB.Bson;
using Xunit;

namespace FileService.Tests;

/// <summary>
/// GridFS/媒体类型路径映射：上传时按扩展名推断 MIME（MongoFileStore.DetectMediaType），
/// 内容句柄为 GridFS ObjectId 字符串（ContentPath / MaterialDocument.GridFsId）。
/// </summary>
public sealed class MediaPathMappingTests
{
    private static string DetectMediaType(string fileName, string? submittedType)
    {
        var method = typeof(MongoFileStore).GetMethod(
            "DetectMediaType",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("MongoFileStore.DetectMediaType not found.");
        return (string)method.Invoke(null, new object?[] { fileName, submittedType })!;
    }

    [Theory]
    [InlineData("notes.txt", null, "text/plain")]
    [InlineData("notes.TXT", "application/octet-stream", "text/plain")]
    [InlineData("readme.md", "", "text/markdown")]
    [InlineData("page.html", null, "text/html")]
    [InlineData("page.htm", "application/octet-stream", "text/html")]
    [InlineData("mail.mhtml", null, "multipart/related")]
    [InlineData("mail.mht", "application/octet-stream", "multipart/related")]
    [InlineData("deck.pptx", null, "application/vnd.openxmlformats-officedocument.presentationml.presentation")]
    [InlineData("photo.jpg", null, "image/jpeg")]
    [InlineData("photo.jpeg", "application/octet-stream", "image/jpeg")]
    [InlineData("scan.png", null, "image/png")]
    public void Extension_maps_to_media_type_when_submitted_type_is_missing_or_generic(
        string fileName,
        string? submitted,
        string expected)
    {
        Assert.Equal(expected, DetectMediaType(fileName, submitted));
    }

    [Theory]
    [InlineData("notes.txt", "text/markdown", "text/markdown")]
    [InlineData("scan.png", "image/jpeg", "image/jpeg")]
    [InlineData("mystery.xyz", "application/x-custom", "application/x-custom")]
    public void Explicit_submitted_content_type_wins_over_extension(
        string fileName,
        string submitted,
        string expected)
    {
        Assert.Equal(expected, DetectMediaType(fileName, submitted));
    }

    [Fact]
    public void Unknown_extension_without_content_type_falls_back_to_octet_stream()
    {
        Assert.Equal("application/octet-stream", DetectMediaType("blob.xyz", null));
        Assert.Equal("application/octet-stream", DetectMediaType("blob.xyz", "   "));
    }

    [Fact]
    public void Grid_fs_object_id_round_trips_through_content_path_string()
    {
        // MongoFileStore 以 gridFsId.ToString() 作为 StoredMaterial.ContentPath，
        // 读取时 ObjectId.TryParse(StoredMaterial.ContentPath) 必须能还原。
        var gridFsId = ObjectId.GenerateNewId();
        var contentPath = gridFsId.ToString();

        Assert.True(ObjectId.TryParse(contentPath, out var parsed));
        Assert.Equal(gridFsId, parsed);
        Assert.False(ObjectId.TryParse("not-an-object-id", out _));
        Assert.False(ObjectId.TryParse("", out _));
    }

    [Fact]
    public void Material_document_maps_material_id_to_grid_fs_id()
    {
        var gridFsId = ObjectId.GenerateNewId();
        var now = DateTimeOffset.UtcNow;
        var material = new Material(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            "Display",
            "notes.txt",
            "text/plain",
            5,
            "abc",
            "UPLOADED",
            null,
            now,
            now);
        var document = new MaterialDocument
        {
            Material = material,
            GridFsId = gridFsId.ToString(),
            SubjectCode = "AGRONOMY",
            ExtractedText = null,
        };

        Assert.Equal(material.MaterialId, document.Material.MaterialId);
        Assert.Equal(gridFsId.ToString(), document.GridFsId);
        Assert.True(ObjectId.TryParse(document.GridFsId, out _));
        Assert.Equal("AGRONOMY", document.SubjectCode);
    }
}
