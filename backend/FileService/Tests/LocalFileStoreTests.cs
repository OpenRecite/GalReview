using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace FileService.Tests;

/// <summary>
/// LocalFileStore 的文件路径映射、checksum 与资料生命周期（内存实现，不起 Mongo）。
/// Mongo 路径将 ContentPath 映射为 GridFS id，本地实现映射为 contentRoot/{materialId}.bin。
/// </summary>
public sealed class LocalFileStoreTests
{
    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [Fact]
    public async Task Create_maps_content_path_under_configured_root()
    {
        var store = TestSupport.NewStore();
        var stored = await store.CreateAsync(
            Guid.NewGuid().ToString(),
            TestSupport.File("notes.txt", "text/plain", "hello"),
            "Notes",
            "AGRONOMY",
            CancellationToken.None);

        var contentRoot = TestSupport.ContentRootOf(store);
        var expected = Path.Combine(contentRoot, stored.Material.MaterialId + ".bin");
        Assert.Equal(expected, stored.ContentPath);
        Assert.True(File.Exists(stored.ContentPath));
        Assert.Equal("AGRONOMY", stored.SubjectCode);
        Assert.Equal("notes.txt", stored.Material.OriginalFileName);
        Assert.Equal("UPLOADED", stored.Material.Status);
    }

    [Fact]
    public async Task Create_computes_lowercase_sha256_checksum()
    {
        var store = TestSupport.NewStore();
        var stored = await store.CreateAsync(
            Guid.NewGuid().ToString(),
            TestSupport.File("a.txt", "text/plain", "hello"),
            "A",
            null,
            CancellationToken.None);

        Assert.Equal(Sha256Hex("hello"), stored.Material.Checksum);
        Assert.Equal(5, stored.Material.SizeBytes);
    }

    [Fact]
    public async Task Create_strips_directory_from_original_file_name()
    {
        var store = TestSupport.NewStore();
        var stored = await store.CreateAsync(
            Guid.NewGuid().ToString(),
            TestSupport.File("folder/sub/notes.txt", "text/plain", "x"),
            "N",
            null,
            CancellationToken.None);

        Assert.Equal("notes.txt", stored.Material.OriginalFileName);
    }

    [Fact]
    public async Task GetMaterial_round_trips_by_id_and_open_content_streams_bytes()
    {
        var store = TestSupport.NewStore();
        var owner = Guid.NewGuid().ToString();
        var stored = await store.CreateAsync(owner, TestSupport.File("a.txt", "text/plain", "payload"), "A", null, CancellationToken.None);

        var material = store.GetMaterial(stored.Material.MaterialId);
        Assert.NotNull(material);
        Assert.Equal(stored.Material.MaterialId, material!.MaterialId);

        using var stream = store.OpenContent(stored.Material.MaterialId);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        Assert.Equal("payload", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task OpenContent_is_null_for_unknown_or_deleted_material()
    {
        var store = TestSupport.NewStore();
        var owner = Guid.NewGuid().ToString();
        var stored = await store.CreateAsync(owner, TestSupport.File("a.txt", "text/plain", "x"), "A", null, CancellationToken.None);

        Assert.Null(store.OpenContent("no-such-id"));
        Assert.True(store.TryDelete(stored.Material.MaterialId, owner, out _));
        Assert.Null(store.OpenContent(stored.Material.MaterialId));
    }

    [Fact]
    public async Task List_pages_with_cursor_and_filters()
    {
        var store = TestSupport.NewStore();
        var owner = Guid.NewGuid().ToString();
        var other = Guid.NewGuid().ToString();
        for (var i = 0; i < 5; i++)
            await store.CreateAsync(owner, TestSupport.File($"m{i}.txt", "text/plain", "x"), $"M{i}", i % 2 == 0 ? "AGRONOMY" : null, CancellationToken.None);
        await store.CreateAsync(other, TestSupport.File("other.txt", "text/plain", "x"), "Other", null, CancellationToken.None);

        var page1 = store.List(owner, null, 2, null, null, out var next);
        Assert.Equal(2, page1.Count);
        Assert.NotNull(next);

        var page2 = store.List(owner, next, 2, null, null, out var next2);
        Assert.Equal(2, page2.Count);
        Assert.NotNull(next2);

        var page3 = store.List(owner, next2, 2, null, null, out var next3);
        Assert.Single(page3);
        Assert.Null(next3);

        // 其他用户的资料不出现在列表中
        Assert.DoesNotContain(page1, x => x.DisplayName == "Other");
        Assert.DoesNotContain(page2, x => x.DisplayName == "Other");

        var filtered = store.List(owner, null, 10, null, "AGRONOMY", out _);
        Assert.Equal(3, filtered.Count);
        Assert.All(filtered, x => Assert.Equal("UPLOADED", x.Status));
    }

    [Fact]
    public async Task List_hides_deleted_and_status_filter_applies()
    {
        var store = TestSupport.NewStore();
        var owner = Guid.NewGuid().ToString();
        var kept = await store.CreateAsync(owner, TestSupport.File("k.txt", "text/plain", "x"), "K", null, CancellationToken.None);
        var dropped = await store.CreateAsync(owner, TestSupport.File("d.txt", "text/plain", "x"), "D", null, CancellationToken.None);
        Assert.True(store.TryDelete(dropped.Material.MaterialId, owner, out _));

        var all = store.List(owner, null, 10, null, null, out _);
        Assert.Single(all);
        Assert.Equal(kept.Material.MaterialId, all[0].MaterialId);

        Assert.Empty(store.List(owner, null, 10, "DELETED", null, out _));
    }

    [Fact]
    public async Task TryDelete_enforces_ownership_and_single_soft_delete()
    {
        var store = TestSupport.NewStore();
        var owner = Guid.NewGuid().ToString();
        var stored = await store.CreateAsync(owner, TestSupport.File("a.txt", "text/plain", "x"), "A", null, CancellationToken.None);
        var id = stored.Material.MaterialId;

        Assert.False(store.TryDelete(id, Guid.NewGuid().ToString(), out _));
        Assert.True(store.TryDelete(id, owner, out var deleted));
        Assert.Equal("DELETED", deleted!.Status);
        Assert.False(store.TryDelete(id, owner, out _));
        Assert.Equal("DELETED", store.GetMaterial(id)!.Status);
    }

    [Fact]
    public async Task Unknown_cursor_yields_empty_page_without_throwing()
    {
        var store = TestSupport.NewStore();
        var owner = Guid.NewGuid().ToString();
        await store.CreateAsync(owner, TestSupport.File("a.txt", "text/plain", "x"), "A", null, CancellationToken.None);

        var page = store.List(owner, "not-a-real-id", 10, null, null, out var next);
        Assert.Empty(page);
        Assert.Null(next);
    }
}
