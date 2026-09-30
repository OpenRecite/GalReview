using Microsoft.Extensions.Primitives;

public static class GalGameHttp
{
    public static bool IsGateway(HttpContext context, string key)
        => context.Request.Headers.TryGetValue("X-Gateway-Key", out var values)
           && values.Count == 1
           && string.Equals(values[0], key, StringComparison.Ordinal);

    public static string? GatewayUser(HttpContext context, string key)
    {
        if (!context.Request.Headers.TryGetValue("X-Gateway-Key", out var gwValues)
            || gwValues.Count != 1
            || !string.Equals(gwValues[0], key, StringComparison.Ordinal))
            return null;

        if (!context.Request.Headers.TryGetValue("X-User-Id", out var userIdValues)
            || userIdValues.Count != 1
            || !TryParseUuidV4(userIdValues[0], out _))
            return null;

        return userIdValues[0];
    }

    public static IResult Failure(HttpContext context, int status, string code, string message, object? details = null)
        => Results.Json(new ApiFailure(null, new ApiError(code, message, details ?? new { }), context.TraceIdentifier), statusCode: status);

    public static bool TryParseUuidV4(string? value, out Guid id)
        => Guid.TryParse(value, out id) && IsUuidV4(id);

    public static bool IsUuidV4(Guid id)
    {
        if (id == Guid.Empty) return false;
        var value = id.ToString("D");
        return value[14] == '4' && value[19] is '8' or '9' or 'a' or 'b';
    }

    public static bool IfNoneMatchMatches(StringValues values, string currentEtag)
    {
        foreach (var rawValue in values)
        {
            if (rawValue is null) continue;
            foreach (var rawTag in rawValue.Split(','))
            {
                var tag = rawTag.Trim();
                if (tag == "*") return true;
                if (tag.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
                    tag = tag[2..].TrimStart();
                if (string.Equals(tag, currentEtag, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }
}
