using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonStructuredLogging();
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(
    options => options.ThrowOnBadRequest = true);

var isMockMode = string.Equals(
    Environment.GetEnvironmentVariable("MOONSTONE_MODE"), "Mock", StringComparison.OrdinalIgnoreCase);
var connectionString = builder.Configuration.GetConnectionString("UserDatabase");
if (!isMockMode && string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings:UserDatabase must be configured.");

var gatewayKey = builder.Configuration["Gateway:ServiceKey"]
    ?? throw new InvalidOperationException("Gateway:ServiceKey must be configured.");
if (builder.Environment.IsProduction() && string.Equals(gatewayKey, "moonstone-local-gateway-key", StringComparison.Ordinal))
    throw new InvalidOperationException("Gateway:ServiceKey must be changed from the development default in production.");
var storageName = isMockMode ? "memory" : "mysql";

if (isMockMode)
{
    builder.Services.AddSingleton<IUserRepository, InMemoryUserRepository>();
}
else
{
    builder.Services.AddSingleton(new UserDatabase(connectionString!));
    builder.Services.AddSingleton<IUserRepository, MySqlUserRepository>();
}

var app = builder.Build();
if (!isMockMode) app.Services.GetRequiredService<UserDatabase>().EnsureCreated();

app.UseRequestLogging("UserService");

app.UseExceptionHandler(error => error.Run(context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    if (exception is BadHttpRequestException or JsonException)
    {
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("UserService")
            .LogWarning(exception, "Invalid UserService request. CorrelationId: {CorrelationId}", context.TraceIdentifier);
        return Results.Json(
            ApiFailure.Create("VALIDATION_ERROR", "请求 JSON、参数或字段格式错误", context.TraceIdentifier),
            statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context);
    }
    context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("UserService")
        .LogError(exception, "Unhandled UserService error. CorrelationId: {CorrelationId}", context.TraceIdentifier);
    return Results.Json(
        ApiFailure.Create("INTERNAL_ERROR", "用户服务暂时不可用", context.TraceIdentifier),
        statusCode: 500).ExecuteAsync(context);
}));

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/healthz" || context.Request.Path == "/readyz")
    {
        await next();
        return;
    }
    if (!GatewayIdentity.IsGateway(context, gatewayKey))
    {
        await GatewayIdentity.Failure(context, 403, "FORBIDDEN", "该服务仅接受经 API Gateway 转发的请求")
            .ExecuteAsync(context);
        return;
    }
    await next();
});

app.MapUserEndpoints(gatewayKey, storageName);
app.Run();

public partial class Program;
