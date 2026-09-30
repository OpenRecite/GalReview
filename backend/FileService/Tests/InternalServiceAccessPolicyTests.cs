using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FileService.Tests;

public sealed class InternalServiceAccessPolicyTests
{
    [Fact]
    public void IsTrusted_accepts_matching_key_and_allowlisted_service()
    {
        var headers = new HeaderDictionary
        {
            ["X-Gateway-Key"] = "secret",
            ["X-Service-Name"] = "KnowledgeService",
        };
        var allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "KnowledgeService", "PracticeService" };
        Assert.True(InternalServiceAccessPolicy.IsTrusted(headers, "secret", allow));
    }

    [Fact]
    public void IsTrusted_rejects_wrong_key_or_unknown_service()
    {
        var headers = new HeaderDictionary
        {
            ["X-Gateway-Key"] = "wrong",
            ["X-Service-Name"] = "KnowledgeService",
        };
        var allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "KnowledgeService" };
        Assert.False(InternalServiceAccessPolicy.IsTrusted(headers, "secret", allow));

        headers["X-Gateway-Key"] = "secret";
        headers["X-Service-Name"] = "EvilService";
        Assert.False(InternalServiceAccessPolicy.IsTrusted(headers, "secret", allow));
    }

    [Fact]
    public void IsTrusted_rejects_duplicate_gateway_key_headers()
    {
        var headers = new HeaderDictionary();
        headers.Append("X-Gateway-Key", "secret");
        headers.Append("X-Gateway-Key", "secret");
        headers["X-Service-Name"] = "KnowledgeService";
        var allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "KnowledgeService" };
        Assert.False(InternalServiceAccessPolicy.IsTrusted(headers, "secret", allow));
    }

    [Fact]
    public void CreateAllowlist_uses_section_when_present()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Access:0"] = "PracticeService",
                ["Access:1"] = "KnowledgeService",
            })
            .Build();
        var allow = InternalServiceAccessPolicy.CreateAllowlist(config.GetSection("Access"), "Fallback");
        Assert.Contains("PracticeService", allow);
        Assert.Contains("KnowledgeService", allow);
        Assert.DoesNotContain("Fallback", allow);
    }
}
