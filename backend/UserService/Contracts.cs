public sealed record CreateUserProfileRequest(string UserId, string DisplayName, string? Locale);
public sealed record AdminProfileLookupRequest(string[]? UserIds);
public sealed record AdminProfileSummary(string UserId, string DisplayName);
public sealed record UpdateUserProfileRequest(string? DisplayName, string? Locale, string[]? PreferredSubjectCodes);
public sealed record UserPreferencesInput(int DailyGoalMinutes, string ContentDifficulty, bool ReducedMotion);
public sealed record UserProfile(string UserId, string DisplayName, string? AvatarUrl, string Locale, string[] PreferredSubjectCodes, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static UserProfile New(string userId, string displayName, string locale) =>
        new(userId, displayName, null, locale, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
public sealed record UserPreferences(int DailyGoalMinutes, string ContentDifficulty, bool ReducedMotion, DateTimeOffset UpdatedAt);
public sealed record ApiError(string Code, string Message, object Details);
public sealed record ApiSuccess(object Data, object Meta, string TraceId)
{
    public static ApiSuccess Create(object data, string traceId) => new(data, new { }, traceId);
}
public sealed record ApiFailure(object? Data, ApiError Error, string TraceId)
{
    public static ApiFailure Create(string code, string message, string traceId) =>
        new(null, new ApiError(code, message, new { }), traceId);
}
