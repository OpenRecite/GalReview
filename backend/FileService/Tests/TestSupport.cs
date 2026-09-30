using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileService.Tests;

/// <summary>Shared in-memory fakes so store tests never touch a real MongoDB or the real disk layout.</summary>
internal static class TestSupport
{
    public static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(x => x.Key, x => x.Value))
            .Build();

    public static LocalFileStore NewStore(string rootPath = "data")
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "galreview-file-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        var environment = new TestHostEnvironment { ContentRootPath = contentRoot };
        var store = new LocalFileStore(
            Config(("FileStorage:RootPath", rootPath)),
            environment,
            NullLogger<LocalFileStore>.Instance);
        return store;
    }

    public static string ContentRootOf(LocalFileStore store, string rootPath = "data")
    {
        // LocalFileStore derives its content root from the host ContentRootPath at construction;
        // recover it from the first stored file path so assertions do not duplicate that logic.
        var field = typeof(LocalFileStore).GetField("_contentRoot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (string)field!.GetValue(store)!;
    }

    public static TestFormFile File(string fileName, string contentType, string content) =>
        new(fileName, contentType, System.Text.Encoding.UTF8.GetBytes(content));

    public static TestFormFile File(string fileName, string contentType, byte[] content) =>
        new(fileName, contentType, content);
}

internal sealed class TestFormFile(string fileName, string contentType, byte[] content) : IFormFile
{
    public string ContentType { get; } = contentType;
    public string ContentDisposition => "form-data";
    public IHeaderDictionary Headers { get; } = new HeaderDictionary();
    public long Length => content.Length;
    public string Name => "file";
    public string FileName { get; } = fileName;

    public void CopyTo(Stream target) => target.Write(content);
    public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default)
    {
        target.Write(content);
        return Task.CompletedTask;
    }

    public Stream OpenReadStream() => new MemoryStream(content, writable: false);
}

internal sealed class TestHostEnvironment : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "GalGame.FileService.Tests";
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
