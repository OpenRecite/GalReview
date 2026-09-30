using System.Text.Json.Serialization;

// ============================================================================
// GalGameService — 游戏生成任务、游戏包与 schema（F15EX 负责）
//
// 端点（§7.1）：
//   POST   /api/v1/game-generations              创建生成任务 (202/422)
//   GET    /api/v1/game-generations/{id}          查询生成任务 (200/404)
//   GET    /api/v1/game-packages/{id}             读取游戏包清单 (200/404)
//   GET    /api/v1/game-packages/{id}/content     下载完整 JSON (200/304/404)
//   POST   /internal/v1/game-package-validations  校验游戏包 (200/422)
// ============================================================================

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonStructuredLogging();

builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(
    options => options.ThrowOnBadRequest = true);

// 枚举序列化为 JSON 字符串（契约要求字符串 token，整数返回 400）
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

var gatewayKey = builder.Configuration["Gateway:ServiceKey"]
    ?? throw new InvalidOperationException("Gateway:ServiceKey must be configured.");
if (builder.Environment.IsProduction() && string.Equals(gatewayKey, "moonstone-local-gateway-key", StringComparison.Ordinal))
    throw new InvalidOperationException("Gateway:ServiceKey must be changed from the development default in production.");
var isMockMode = string.Equals(
    builder.Configuration["MOONSTONE_MODE"] ?? Environment.GetEnvironmentVariable("MOONSTONE_MODE"),
    "Mock", StringComparison.OrdinalIgnoreCase);
var useMongoStore = !string.Equals(
    builder.Configuration["GalGameStore:Provider"] ?? Environment.GetEnvironmentVariable("GalGameStore__Provider"),
    "Memory", StringComparison.OrdinalIgnoreCase);
var storageName = (isMockMode, useMongoStore) switch
{
    (true, true) => "mock-mongodb",
    (true, false) => "mock-memory",
    (false, true) => "mongodb",
    (false, false) => "ephemeral-memory",
};

// 叙事生成配置（§7.3.2）
var narrativeSection = builder.Configuration.GetSection(NarrativeGenerationOptions.SectionName);
var narrativeOptions = narrativeSection.Get<NarrativeGenerationOptions>() ?? new NarrativeGenerationOptions();
// 本地 dotnet run 不会像 Compose 那样自动把 DEEPSEEK_API_KEY 映射到配置节；允许直接从
// 进程环境读取，但绝不要求开发者把密钥写进 appsettings.json。
if (string.IsNullOrWhiteSpace(narrativeOptions.ApiKey))
    narrativeOptions.ApiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? string.Empty;
// Mock 模式强制关闭外部模型调用
if (isMockMode)
    narrativeOptions.Enabled = false;
// 注册为单例供 DeepSeekNarrativeClient 和 NarrativeGenerationService 直接注入
builder.Services.AddSingleton(narrativeOptions);

var narrativeEnabled = narrativeOptions.CanCallProvider;
var narrativeModel = narrativeOptions.Model;
var narrativePromptVersion = narrativeOptions.PromptVersion;

var voiceSection = builder.Configuration.GetSection(VoiceSynthesisOptions.SectionName);
var voiceOptions = voiceSection.Get<VoiceSynthesisOptions>() ?? new VoiceSynthesisOptions();
if (string.IsNullOrWhiteSpace(voiceOptions.ApiKey))
    voiceOptions.ApiKey = Environment.GetEnvironmentVariable("MIMO_API_KEY") ?? string.Empty;
if (isMockMode)
    voiceOptions.Enabled = false;
builder.Services.AddSingleton(voiceOptions);
var voiceEnabled = voiceOptions.CanCallProvider;

var characterVoiceCatalog = CharacterVoiceCatalog.Load(Path.Combine(
    builder.Environment.ContentRootPath,
    CharacterVoiceCatalog.DefaultFileName));
builder.Services.AddSingleton(characterVoiceCatalog);

var validationAllowedServices = InternalServiceAccessPolicy.CreateAllowlist(
    builder.Configuration.GetSection("InternalAccess:ValidationAllowedServices"),
    "RenderService");
var packageReaderAllowedServices = InternalServiceAccessPolicy.CreateAllowlist(
    builder.Configuration.GetSection("InternalAccess:PackageReaderAllowedServices"),
    "RenderService");

// HttpClient：经 Gateway 调用 KnowledgeService
builder.Services.AddHttpClient("gateway", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Gateway:BaseUrl"] ?? "http://localhost:5000");
    client.Timeout = TimeSpan.FromSeconds(45);
});

// HttpClient：叙事生成模型调用（DeepSeek 等 OpenAI 兼容端点）
builder.Services.AddHttpClient("narrative", client =>
{
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(narrativeOptions.TimeoutSeconds, 10, 300));
    client.MaxResponseContentBufferSize = 2 * 1024 * 1024; // 2 MiB 安全上限
});

builder.Services.AddHttpClient("mimo-tts", client =>
{
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(voiceOptions.TimeoutSeconds, 10, 300));
    client.MaxResponseContentBufferSize = Math.Clamp(
        voiceOptions.MaxAudioBytes * 2L,
        128 * 1024L,
        24 * 1024 * 1024L);
});

// DI 注册
builder.Services.AddSingleton<GamePackageValidator>();
builder.Services.AddSingleton<NarrativePromptBuilder>();
builder.Services.AddSingleton<NarrativeDraftValidator>();
builder.Services.AddSingleton<INarrativeModelClient>(sp => new DeepSeekNarrativeClient(
    sp.GetRequiredService<IHttpClientFactory>(),
    narrativeOptions));
builder.Services.AddSingleton<NarrativeGenerationService>();
builder.Services.AddSingleton<IGameCreditBilling, CreditBillingClient>();
builder.Services.AddSingleton<ITtsClient>(sp => new MiMoTtsClient(
    sp.GetRequiredService<IHttpClientFactory>(),
    voiceOptions));
builder.Services.AddSingleton<PackageVoiceService>();
if (useMongoStore)
{
    builder.Services.AddSingleton<IGameStore>(sp => new MongoGameStore(
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<MongoGameStore>(),
        seedGoldenPackage: isMockMode));
}
else
{
    builder.Services.AddSingleton<IGameStore>(_ => new InMemoryGameStore(isMockMode));
}
builder.Services.AddSingleton<PlanGraphClient>(sp => new PlanGraphClient(
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<PlanGraphClient>(),
    sp.GetRequiredService<IConfiguration>(),
    isMockMode));
builder.Services.AddSingleton<GameGenerator>();
builder.Services.AddSingleton<GameGenerationQueue>();
builder.Services.AddHostedService<GameGenerationWorker>();

var app = builder.Build();

if (narrativeOptions.Enabled && !narrativeEnabled)
{
    app.Logger.LogWarning(
        "Narrative generation was requested but provider configuration is incomplete; set DEEPSEEK_API_KEY and a valid HTTPS endpoint to avoid deterministic fallback");
}
else if (narrativeEnabled)
{
    app.Logger.LogInformation(
        "Narrative generation enabled: model={Model}, prompt={PromptVersion}, providerAttempts={ProviderAttempts}, draftAttempts={DraftAttempts}",
        narrativeModel,
        narrativePromptVersion,
        Math.Clamp(narrativeOptions.MaxProviderAttempts, 1, 4),
        Math.Clamp(narrativeOptions.MaxDraftAttempts, 1, 3));
}

if (voiceOptions.Enabled && !voiceEnabled)
{
    app.Logger.LogWarning(
        "MiMo voice synthesis was requested but provider configuration is incomplete; set MIMO_API_KEY to enable dialogue audio");
}
else if (voiceEnabled)
{
    app.Logger.LogInformation(
        "MiMo voice synthesis enabled: model={Model}; concurrency={Concurrency}",
        voiceOptions.Model,
        Math.Clamp(voiceOptions.MaxConcurrency, 1, 6));
}

await GalGameStartupRecovery.RecoverStaleJobsAsync(app, useMongoStore);

app.UseGalGameCorrelationId();
app.UseRequestLogging("GalGameService", normalizeCorrelationId: false);
// 出站透传：CreditBillingClient 等读 GalGameTraceFlow.Current
RequestLogging.BeginAmbientTrace = GalGameTraceFlow.Begin;
app.UseGalGameExceptionHandler();
app.UseGalGameGatewayKey(gatewayKey);

// ============================================================================
// 健康检查
// ============================================================================

app.MapGet("/healthz", (HttpContext c) =>
    Results.Ok(ApiSuccess.Create(new { status = "live" }, c.TraceIdentifier)));
app.MapGet("/readyz", (HttpContext c, IGameStore store) =>
{
    if (useMongoStore && store is MongoGameStore mongoStore)
    {
        var ready = mongoStore.IsReady();
        if (!ready)
            return Results.Json(ApiFailure.Create("NOT_READY", "MongoDB 未就绪", c.TraceIdentifier), statusCode: 503);
    }
    return Results.Ok(ApiSuccess.Create(new
    {
        status = "ready",
        storage = storageName,
        narrativeEnabled,
        narrativeModel,
        narrativePromptVersion,
    }, c.TraceIdentifier));
});

// 端点 1-2：生成任务创建 / 查询（GameGenerationEndpoints）
app.MapGameGenerationEndpoints(gatewayKey, isMockMode, narrativeEnabled, narrativeOptions);

// 端点 3-5：游戏包读取 / 音频 / INTERNAL 校验（已拆至 GamePackageEndpoints）
app.MapGamePackageEndpoints(gatewayKey, packageReaderAllowedServices, validationAllowedServices);

app.Run();

// 暴露 partial class 供 WebApplicationFactory 集成测试使用
public partial class Program { }
