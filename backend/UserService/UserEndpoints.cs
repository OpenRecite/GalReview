using System.Text.Json;
using Microsoft.AspNetCore.Http;

public static class UserEndpoints
{
    public static void MapUserEndpoints(this WebApplication app, string gatewayKey, string storageName)
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        app.MapGet("/healthz", (HttpContext c) =>
            Results.Ok(ApiSuccess.Create(new { status = "live" }, c.TraceIdentifier)));
        app.MapGet("/readyz", (HttpContext c) =>
            Results.Ok(ApiSuccess.Create(new { status = "ready", storage = storageName }, c.TraceIdentifier)));

        app.MapPost("/internal/v1/users", (CreateUserProfileRequest request, HttpContext c, IUserRepository users) =>
        {
            if (!GatewayIdentity.IsGateway(c, gatewayKey)
                || !string.Equals(c.Request.Headers["X-Service-Name"], "AuthService", StringComparison.Ordinal))
                return GatewayIdentity.Failure(c, 403, "FORBIDDEN", "仅允许经 Gateway 的 AuthService 创建用户资料");
            if (!Guid.TryParse(request.UserId, out _) || !UserProfileValidation.IsValidDisplayName(request.DisplayName))
                return GatewayIdentity.Failure(c, 400, "VALIDATION_ERROR", "userId 和 displayName 不符合要求");

            var profile = UserProfile.New(request.UserId, request.DisplayName.Trim(), GatewayIdentity.NormalizeLocale(request.Locale));
            return users.TryCreate(profile)
                ? Results.Created($"/api/v1/users/{profile.UserId}", ApiSuccess.Create(profile, c.TraceIdentifier))
                : GatewayIdentity.Failure(c, 409, "STATE_CONFLICT", "用户资料已存在");
        });

        app.MapPost("/internal/v1/users/profile-lookups", (AdminProfileLookupRequest request, HttpContext c, IUserRepository users) =>
        {
            if (!GatewayIdentity.IsAuthService(c, gatewayKey))
                return GatewayIdentity.Failure(c, 403, "FORBIDDEN", "仅允许经 Gateway 的 AuthService 查询用户资料");
            return AdminProfileLookupHandler.Handle(request, c, users);
        });

        app.MapDelete("/internal/v1/users/{userId}", (string userId, HttpContext c, IUserRepository users) =>
        {
            if (!GatewayIdentity.IsAuthService(c, gatewayKey))
                return GatewayIdentity.Failure(c, 403, "FORBIDDEN", "仅允许经 Gateway 的 AuthService 删除用户资料");
            if (!Guid.TryParse(userId, out _))
                return GatewayIdentity.Failure(c, 400, "VALIDATION_ERROR", "userId 不符合要求");
            return users.DeleteProfile(userId)
                ? Results.NoContent()
                : GatewayIdentity.Failure(c, 404, "RESOURCE_NOT_FOUND", "用户资料不存在");
        });

        app.MapGet("/api/v1/users/me", (HttpContext c, IUserRepository users) =>
        {
            var userId = GatewayIdentity.GetUserId(c, gatewayKey);
            return userId is null
                ? GatewayIdentity.Failure(c, 401, "AUTH_REQUIRED", "需要有效登录状态")
                : users.FindProfile(userId) is { } profile
                    ? Results.Ok(ApiSuccess.Create(profile, c.TraceIdentifier))
                    : GatewayIdentity.Failure(c, 404, "RESOURCE_NOT_FOUND", "用户资料不存在");
        });

        app.MapMethods("/api/v1/users/me", ["PATCH", "PUT"], async (HttpContext c, IUserRepository users) =>
        {
            var userId = GatewayIdentity.GetUserId(c, gatewayKey);
            if (userId is null)
                return GatewayIdentity.Failure(c, 401, "AUTH_REQUIRED", "需要有效登录状态");
            return await UserProfileUpdateHandler.HandleAsync(userId, c, users, jsonOptions);
        });

        app.MapGet("/api/v1/users/me/preferences", (HttpContext c, IUserRepository users) =>
        {
            var userId = GatewayIdentity.GetUserId(c, gatewayKey);
            return userId is null
                ? GatewayIdentity.Failure(c, 401, "AUTH_REQUIRED", "需要有效登录状态")
                : users.FindPreferences(userId) is { } preferences
                    ? Results.Ok(ApiSuccess.Create(preferences, c.TraceIdentifier))
                    : GatewayIdentity.Failure(c, 404, "RESOURCE_NOT_FOUND", "用户资料不存在");
        });

        app.MapPut("/api/v1/users/me/preferences", async (HttpContext c, IUserRepository users) =>
        {
            var userId = GatewayIdentity.GetUserId(c, gatewayKey);
            if (userId is null)
                return GatewayIdentity.Failure(c, 401, "AUTH_REQUIRED", "需要有效登录状态");
            return await UserPreferencesUpdateHandler.HandleAsync(userId, c, users, jsonOptions);
        });
    }
}
