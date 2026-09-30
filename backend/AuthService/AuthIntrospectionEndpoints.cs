public static class AuthIntrospectionEndpoints
{
    public static void MapAuthIntrospectionEndpoints(this WebApplication app, string gatewayKey)
    {
        app.MapPost("/internal/v1/auth/introspections", (TokenIntrospectionRequest request, HttpContext c, IAuthRepository repository) =>
        {
            if (!AuthHttp.IsGateway(c, gatewayKey)) return AuthHttp.Failure(c, 403, "FORBIDDEN", "仅允许 Gateway 调用令牌内省接口");
            var session = string.IsNullOrWhiteSpace(request.Token)
                ? null
                : repository.TouchAccessToken(request.Token);
            var result = session is not null
                ? new TokenIntrospection(true, session.UserId, session.SessionId, ["user"], session.AccessExpiresAt)
                : new TokenIntrospection(false, null, null, [], null);
            return Results.Ok(ApiSuccess.Create(result, c.TraceIdentifier));
        });
    }
}
