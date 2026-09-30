using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

public static class AuthRegistrationEndpoints
{
    public static void MapAuthRegistrationEndpoints(this WebApplication app, string gatewayKey)
    {
        app.MapPost("/api/v1/auth/registrations", async (RegistrationRequest request, HttpContext c, IAuthRepository repository, IPasswordHasher<Credential> hasher, IHttpClientFactory clients) =>
        {
            if (!AuthHttp.ValidEmail(request.Email) || !AuthHttp.ValidName(request.DisplayName) || !AuthHttp.ValidPassword(request.Password)) return AuthHttp.Failure(c, 400, "VALIDATION_ERROR", "邮箱、显示名或密码格式不正确");
            var credential = Credential.New(request.Email.Trim().ToLowerInvariant());
            credential = credential with { PasswordHash = hasher.HashPassword(credential, request.Password) };
            var registration = repository.TryCreateCredential(credential);
            if (registration == RegistrationOutcome.EmailAlreadyRegistered) return AuthHttp.Failure(c, 409, "STATE_CONFLICT", "该邮箱已注册");
            // Registration compensation must finish even when the browser disconnects;
            // do not let RequestAborted leave credentials, profile and credits half-created.
            var profileCreated = await AuthGatewayClient.CreateProfileAsync(clients.CreateClient("gateway"), gatewayKey, c.TraceIdentifier, credential.UserId, request.DisplayName.Trim(), CancellationToken.None);
            if (!profileCreated) { repository.DeleteCredential(credential.UserId); return AuthHttp.Failure(c, 503, "SERVICE_UNAVAILABLE", "用户资料服务暂时不可用"); }
            var creditsCreated = await AuthGatewayClient.CreateCreditAccountAsync(clients.CreateClient("gateway"), gatewayKey, c.TraceIdentifier, credential.UserId, CancellationToken.None);
            if (!creditsCreated)
            {
                await AuthGatewayClient.DeleteUserProfileAsync(clients.CreateClient("gateway"), gatewayKey, c.TraceIdentifier, credential.UserId, CancellationToken.None);
                repository.DeleteCredential(credential.UserId);
                return AuthHttp.Failure(c, 503, "SERVICE_UNAVAILABLE", "credits 服务暂时不可用");
            }
            StoredSession session;
            try { session = repository.CreateSession(credential.UserId, request.DeviceName); }
            catch
            {
                await AuthGatewayClient.DeleteUserProfileAsync(clients.CreateClient("gateway"), gatewayKey, c.TraceIdentifier, credential.UserId, CancellationToken.None);
                await AuthGatewayClient.DeleteCreditAccountAsync(clients.CreateClient("gateway"), gatewayKey, c.TraceIdentifier, credential.UserId, CancellationToken.None);
                repository.DeleteCredential(credential.UserId);
                throw;
            }
            return Results.Created($"/api/v1/auth/sessions/{session.SessionId}", ApiSuccess.Create(session.ToResponse(), c.TraceIdentifier));
        });

        app.MapDelete("/api/v1/auth/account", async ([FromBody] AccountDeletionRequest request, HttpContext c, IAuthRepository repository, IAdminAuditRepository audit, IPasswordHasher<Credential> hasher, IHttpClientFactory clients) =>
        {
            var userId = AuthHttp.GetGatewayUser(c, gatewayKey);
            if (userId is null) return AuthHttp.Failure(c, 401, "AUTH_REQUIRED", "登陆状态已失效");
            if (AuthHttp.IsAdmin(c, gatewayKey)) return AuthHttp.Failure(c, 403, "FORBIDDEN", "管理员不能通过此接口注销");
            if (string.IsNullOrWhiteSpace(request.CurrentPassword)) return AuthHttp.Failure(c, 400, "VALIDATION_ERROR", "请输入当前登录密码以确认注销");

            var credential = repository.FindCredentialById(userId);
            if (credential is null) return AuthHttp.Failure(c, 404, "RESOURCE_NOT_FOUND", "用户不存在");
            if (!AuthHttp.PasswordMatches(hasher, credential, request.CurrentPassword)) return AuthHttp.Failure(c, 401, "AUTH_REQUIRED", "当前密码错误");
            if (!await AuthGatewayClient.DeleteUserProfileAsync(clients.CreateClient("gateway"), gatewayKey, c.TraceIdentifier, userId, c.RequestAborted))
            {
                audit.Write(AdminAuditRecord.Create(userId, "ACCOUNT_SELF_DELETE", userId, null, "PROFILE_DELETE_FAILED", c.TraceIdentifier));
                return AuthHttp.Failure(c, 503, "SERVICE_UNAVAILABLE", "用户资料服务暂时不可用，注销失败");
            }

            var deleted = repository.DeleteAccount(userId);
            audit.Write(AdminAuditRecord.Create(userId, "ACCOUNT_SELF_DELETE", userId, null, deleted ? "SUCCEEDED" : "NOT_FOUND", c.TraceIdentifier));
            return deleted ? Results.NoContent() : AuthHttp.Failure(c, 404, "RESOURCE_NOT_FOUND", "用户不存在");
        });
    }
}
