using Microsoft.AspNetCore.Identity;

public static class AuthPasswordEndpoints
{
    public static void MapAuthPasswordEndpoints(this WebApplication app, string gatewayKey)
    {
        app.MapPost("/api/v1/auth/password-changes", (PasswordChangeRequest request, HttpContext c, IAuthRepository repository, IPasswordHasher<Credential> hasher) =>
        {
            var userId = AuthHttp.GetGatewayUser(c, gatewayKey);
            if (userId is null) return AuthHttp.Failure(c, 401, "AUTH_REQUIRED", "登录状态已失效");
            if (string.IsNullOrWhiteSpace(request.CurrentPassword)) return AuthHttp.Failure(c, 400, "VALIDATION_ERROR", "请输入当前密码");
            if (!AuthHttp.ValidPassword(request.NewPassword)) return AuthHttp.Failure(c, 400, "VALIDATION_ERROR", "新密码至少需要 8 个字符");
            var credential = repository.FindCredentialById(userId);
            if (credential is null) return AuthHttp.Failure(c, 404, "RESOURCE_NOT_FOUND", "用户不存在");
            if (!AuthHttp.PasswordMatches(hasher, credential, request.CurrentPassword)) return AuthHttp.Failure(c, 401, "AUTH_REQUIRED", "当前密码错误");
            repository.UpdatePassword(credential with { PasswordHash = hasher.HashPassword(credential, request.NewPassword!) });
            repository.RevokeAllSessions(userId);
            return Results.NoContent();
        });
        app.MapPost("/api/v1/auth/password-reset-requests", async (PasswordResetRequest request, HttpContext c, IAuthRepository repository, PasswordResetEmailSender emailSender) =>
        {
            if (!AuthHttp.ValidEmail(request.Email)) return AuthHttp.Failure(c, 400, "VALIDATION_ERROR", "请输入有效的邮箱地址");
            var credential = repository.FindCredential(request.Email.Trim().ToLowerInvariant());
            // Always return 202 Accepted to prevent email enumeration.
            // If the email is registered, a reset token is sent; otherwise, no action is taken.
            if (credential is not null)
            {
                var resetToken = repository.CreatePasswordReset(credential.UserId);
                var delivered = await emailSender.SendAsync(credential.Email, resetToken, c.TraceIdentifier, c.RequestAborted);
                if (!delivered) repository.DeletePasswordReset(resetToken);
            }
            return Results.Accepted();
        });
        app.MapPost("/api/v1/auth/password-resets", (PasswordResetConfirmation request, HttpContext c, IAuthRepository repository, IPasswordHasher<Credential> hasher) =>
        {
            if (!AuthHttp.ValidPassword(request.NewPassword)) return AuthHttp.Failure(c, 422, "BUSINESS_RULE_VIOLATION", "新密码不符合安全要求");
            if (string.IsNullOrWhiteSpace(request.ResetToken)) return AuthHttp.Failure(c, 422, "BUSINESS_RULE_VIOLATION", "重置令牌无效或已过期");
            var credential = repository.ConsumePasswordReset(request.ResetToken);
            if (credential is null) return AuthHttp.Failure(c, 422, "BUSINESS_RULE_VIOLATION", "重置令牌无效或已过期");
            repository.UpdatePassword(credential with { PasswordHash = hasher.HashPassword(credential, request.NewPassword!) });
            repository.RevokeAllSessions(credential.UserId);
            return Results.NoContent();
        }).RequireRateLimiting("password-reset-confirmation");
    }
}
