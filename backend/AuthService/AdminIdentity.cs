internal static class AdminIdentity
{
    /// <summary>联调专用默认管理员；生产环境必须通过 Admin:PrincipalId 显式配置。</summary>
    public const string DevDefaultUserId = "00000000-0000-4000-8000-000000000001";

    public static string UserId { get; private set; } = DevDefaultUserId;

    /// <summary>管理员身份是否来自显式配置（/readyz 上报用）。</summary>
    public static bool IsExplicitlyConfigured { get; private set; }

    public static void Configure(string? configuredPrincipalId, bool isProduction)
    {
        if (Guid.TryParse(configuredPrincipalId, out var parsed) && parsed != Guid.Empty)
        {
            UserId = parsed.ToString("D");
            IsExplicitlyConfigured = true;
            return;
        }

        if (isProduction)
        {
            throw new InvalidOperationException(
                "Admin:PrincipalId must be configured in production.");
        }

        UserId = DevDefaultUserId;
        IsExplicitlyConfigured = false;
    }
}
