using Microsoft.AspNetCore.Identity;

public static class AuthSessionEndpoints
{
    public static void MapAuthSessionEndpoints(this WebApplication app, string gatewayKey)
    {
        app.MapPost("/api/v1/auth/sessions", (LoginRequest request, HttpContext c, IAuthRepository repository, IPasswordHasher<Credential> hasher) =>
        {
            if (!AuthHttp.ValidEmail(request.Email) || string.IsNullOrEmpty(request.Password)) return AuthHttp.Failure(c, 400, "VALIDATION_ERROR", "邮箱和密码不能为空");
            var credential = repository.FindCredential(request.Email.Trim().ToLowerInvariant());
            if (credential is null || !AuthHttp.PasswordMatches(hasher, credential, request.Password)) return AuthHttp.Failure(c, 401, "AUTH_REQUIRED", "邮箱或密码错误");
            var session = repository.CreateSession(credential.UserId, request.DeviceName);
            return Results.Created($"/api/v1/auth/sessions/{session.SessionId}", ApiSuccess.Create(session.ToResponse(), c.TraceIdentifier));
        });
        app.MapGet("/api/v1/auth/sessions/{sessionId}", (string sessionId, HttpContext c, IAuthRepository repository) =>
        {
            var caller = AuthHttp.GetGatewayUser(c, gatewayKey); if (caller is null) return AuthHttp.Failure(c, 401, "AUTH_REQUIRED", "登录状态已失效");
            var session = repository.FindSession(sessionId); return session is null || session.UserId != caller ? AuthHttp.Failure(c, 404, "RESOURCE_NOT_FOUND", "会话不存在") : Results.Ok(ApiSuccess.Create(session.ToContract(), c.TraceIdentifier));
        });
        app.MapDelete("/api/v1/auth/sessions/{sessionId}", (string sessionId, HttpContext c, IAuthRepository repository) =>
        {
            var caller = AuthHttp.GetGatewayUser(c, gatewayKey); if (caller is null) return AuthHttp.Failure(c, 401, "AUTH_REQUIRED", "登录状态已失效");
            return repository.RevokeSession(sessionId, caller) ? Results.NoContent() : AuthHttp.Failure(c, 404, "RESOURCE_NOT_FOUND", "会话不存在");
        });
        app.MapPost("/api/v1/auth/tokens", (RefreshTokenRequest request, HttpContext c, IAuthRepository repository) =>
        {
            var session = string.IsNullOrWhiteSpace(request.RefreshToken)
                ? null
                : repository.Rotate(request.RefreshToken);
            return session is null
                ? AuthHttp.Failure(c, 401, "AUTH_REQUIRED", "刷新令牌无效")
                : Results.Created("/api/v1/auth/tokens", ApiSuccess.Create(session.ToTokenPair(), c.TraceIdentifier));
        });
    }
}
