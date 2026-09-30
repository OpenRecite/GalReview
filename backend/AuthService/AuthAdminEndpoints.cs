using Microsoft.AspNetCore.Identity;

public static class AuthAdminEndpoints
{
    public static void MapAuthAdminEndpoints(this WebApplication app, string gatewayKey, AuthAdminCredentials adminCredentials)
    {
        app.MapPost("/api/v1/admin/sessions", (AdminLoginRequest request, HttpContext c, IAuthRepository repository, IPasswordHasher<Credential> hasher) =>
        {
            if (!AuthHttp.FixedTimeEquals(request.Username?.Trim() ?? string.Empty, adminCredentials.Username))
                return AuthHttp.Failure(c, 401, "AUTH_REQUIRED", "管理员账号或密码错误");

            // Prefer hashed password (Admin:PasswordHash). Fall back to legacy plaintext
            // (Admin:Password) only if the hash is not configured, using FixedTimeEquals.
            var password = request.Password ?? string.Empty;
            var passwordValid = false;
            if (!string.IsNullOrWhiteSpace(adminCredentials.PasswordHash))
            {
                var adminCredential = new Credential(AdminIdentity.UserId, "admin", adminCredentials.PasswordHash);
                try
                {
                    passwordValid = hasher.VerifyHashedPassword(adminCredential, adminCredentials.PasswordHash, password) is not PasswordVerificationResult.Failed;
                }
                catch (FormatException) { passwordValid = false; }
                catch (ArgumentException) { passwordValid = false; }
            }
            else
            {
                passwordValid = AuthHttp.FixedTimeEquals(password, adminCredentials.PasswordLegacy!);
            }

            if (!passwordValid)
                return AuthHttp.Failure(c, 401, "AUTH_REQUIRED", "管理员账号或密码错误");
            var session = repository.CreateSession(AdminIdentity.UserId, "MoonStone admin");
            return Results.Created($"/api/v1/auth/sessions/{session.SessionId}", ApiSuccess.Create(session.ToResponse(), c.TraceIdentifier));
        });
        app.MapGet("/api/v1/admin/users", async (HttpContext c, IAdminRepository admin, IHttpClientFactory clients) =>
        {
            if (!AuthHttp.IsAdmin(c, gatewayKey)) return AuthHttp.Failure(c, 403, "FORBIDDEN", "需要管理员权限");
            var accounts = admin.ListUsers();
            var userIds = accounts.Select(account => account.Id).ToArray();
            var displayNamesTask = AuthGatewayClient.LookupProfileDisplayNamesAsync(clients.CreateClient("gateway"), gatewayKey, c.TraceIdentifier, userIds, c.RequestAborted);
            var creditBalancesTask = AuthGatewayClient.LookupCreditBalancesAsync(clients.CreateClient("gateway"), gatewayKey, c.TraceIdentifier, userIds, c.RequestAborted);
            await Task.WhenAll(displayNamesTask, creditBalancesTask);
            var displayNames = await displayNamesTask;
            if (displayNames is null) return AuthHttp.Failure(c, 503, "SERVICE_UNAVAILABLE", "用户资料服务暂时不可用");
            var creditBalances = await creditBalancesTask;
            if (creditBalances is null) return AuthHttp.Failure(c, 503, "SERVICE_UNAVAILABLE", "credits 服务暂时不可用");
            var users = accounts.Select(account => new AdminUser(account.Id, account.Email, displayNames.GetValueOrDefault(account.Id, account.Email), true, creditBalances[account.Id])).ToArray();
            return Results.Ok(ApiSuccess.Create(users, c.TraceIdentifier));
        });
        app.MapDelete("/api/v1/admin/users/{userId}", async (string userId, HttpContext c, IAdminRepository admin, IAdminAuditRepository audit, IHttpClientFactory clients) =>
        {
            if (!AuthHttp.IsAdmin(c, gatewayKey)) return AuthHttp.Failure(c, 403, "FORBIDDEN", "需要管理员权限");
            var actorUserId = AuthHttp.GetGatewayUser(c, gatewayKey)!;
            if (!admin.UserExists(userId))
            {
                audit.Write(AdminAuditRecord.Create(actorUserId, "USER_DELETE", userId, null, "NOT_FOUND", c.TraceIdentifier));
                return AuthHttp.Failure(c, 404, "RESOURCE_NOT_FOUND", "用户不存在");
            }
            if (!await AuthGatewayClient.DeleteUserProfileAsync(clients.CreateClient("gateway"), gatewayKey, c.TraceIdentifier, userId, c.RequestAborted))
            {
                audit.Write(AdminAuditRecord.Create(actorUserId, "USER_DELETE", userId, null, "PROFILE_DELETE_FAILED", c.TraceIdentifier));
                return AuthHttp.Failure(c, 503, "SERVICE_UNAVAILABLE", "用户资料服务暂时不可用");
            }
            var deleted = admin.DeleteAuthUser(userId);
            audit.Write(AdminAuditRecord.Create(actorUserId, "USER_DELETE", userId, null, deleted ? "SUCCEEDED" : "NOT_FOUND", c.TraceIdentifier));
            return deleted ? Results.NoContent() : AuthHttp.Failure(c, 404, "RESOURCE_NOT_FOUND", "用户不存在");
        });
        app.MapPost("/api/v1/admin/users/{userId}/password", (string userId, AdminResetPasswordRequest request, HttpContext c, IAuthRepository repository, IAdminAuditRepository audit, IPasswordHasher<Credential> hasher) =>
        {
            if (!AuthHttp.IsAdmin(c, gatewayKey)) return AuthHttp.Failure(c, 403, "FORBIDDEN", "需要管理员权限");
            if (!AuthHttp.ValidPassword(request.NewPassword)) return AuthHttp.Failure(c, 400, "VALIDATION_ERROR", "新密码至少需要 8 个字符");
            var credential = repository.FindCredentialById(userId);
            if (credential is null)
            {
                audit.Write(AdminAuditRecord.Create(AuthHttp.GetGatewayUser(c, gatewayKey)!, "USER_PASSWORD_RESET", userId, null, "NOT_FOUND", c.TraceIdentifier));
                return AuthHttp.Failure(c, 404, "RESOURCE_NOT_FOUND", "用户不存在");
            }
            repository.UpdatePassword(credential with { PasswordHash = hasher.HashPassword(credential, request.NewPassword) });
            repository.RevokeAllSessions(userId);
            audit.Write(AdminAuditRecord.Create(AuthHttp.GetGatewayUser(c, gatewayKey)!, "USER_PASSWORD_RESET", userId, null, "SUCCEEDED", c.TraceIdentifier));
            return Results.NoContent();
        });
    }
}
