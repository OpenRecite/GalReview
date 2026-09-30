using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelService.Application;
using ModelService.Persistence;
using Xunit;

namespace ModelService.Tests.Persistence;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void Configured_resource_root_overrides_release_content_root()
    {
        var contentRoot = Path.GetFullPath(Path.Combine("release", "model-service"));
        var sharedRoot = Path.GetFullPath(Path.Combine(".production", "shared", "model-resources"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ModelResources:RootPath"] = sharedRoot
            })
            .Build();
        var services = new ServiceCollection();

        services.AddModelPersistence(configuration, contentRoot);
        using var provider = services.BuildServiceProvider();

        var catalog = provider.GetRequiredService<ModelAssetCatalog>();
        Assert.Equal(
            Path.Combine(sharedRoot, "Models", "multilingual-minilm-nli", "model.onnx"),
            catalog.NliModelPath);
        // 引擎与负载指标必须是同一单例，但此处不实例化（构造需要 ILogger）
        var descriptors = services.Where(d =>
                d.ServiceType == typeof(IFacetInferenceEngine)
                || d.ServiceType == typeof(IInferenceLoadStats)
                || d.ServiceType == typeof(MultilingualNliInferenceEngine))
            .ToArray();
        Assert.Equal(3, descriptors.Length);
    }
}
