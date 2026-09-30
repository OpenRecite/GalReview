using System.Security.Cryptography;

public static class GatewayIdentity
{
    public static bool IsGateway(HttpContext context, string key)
    {
        var values = context.Request.Headers["X-Gateway-Key"];
        return values.Count == 1 && FixedTimeEquals(values[0]!, key);
    }

    public static bool IsAuthService(HttpContext context, string key) =>
        IsGateway(context, key)
        && string.Equals(context.Request.Headers["X-Service-Name"], "AuthService", StringComparison.Ordinal);

    public static string? GetUserId(HttpContext context, string key)
    {
        var userId = context.Request.Headers["X-User-Id"].FirstOrDefault();
        return IsGateway(context, key) && !string.IsNullOrWhiteSpace(userId) ? userId : null;
    }

    public static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        var length = Math.Max(leftBytes.Length, rightBytes.Length);
        var paddedLeft = new byte[length];
        var paddedRight = new byte[length];
        leftBytes.CopyTo(paddedLeft, 0);
        rightBytes.CopyTo(paddedRight, 0);
        return CryptographicOperations.FixedTimeEquals(paddedLeft, paddedRight)
            && leftBytes.Length == rightBytes.Length;
    }

    public static IResult Failure(HttpContext context, int status, string code, string message) =>
        Results.Json(ApiFailure.Create(code, message, context.TraceIdentifier), statusCode: status);

    public static string NormalizeLocale(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "zh-CN" : value.Trim();
}
