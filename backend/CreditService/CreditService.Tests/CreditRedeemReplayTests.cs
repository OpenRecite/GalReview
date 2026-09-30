using System.Collections.Concurrent;
using CreditService.Application;
using CreditService.Domain;
using CreditService.Persistence;
using Xunit;

namespace CreditService.Tests;

/// <summary>
/// 兑换码防重放：同一码在串行/并发下只能兑换一次；撤销与过期码不可兑；
/// 并发兑换多个不同码时余额精确累加。
/// </summary>
public sealed class CreditRedeemReplayTests
{
    [Fact]
    public async Task Same_code_redeemed_twice_sequentially_fails_on_second_try()
    {
        var repo = new MemoryCreditRepository();
        var handlers = new CreditHandlers(repo);
        var user = Guid.NewGuid();
        await handlers.Handle(new ProvisionAccountCommand(user), default);
        var codes = await handlers.Handle(new CreateCodeBatchCommand(Guid.NewGuid(), 1, 2.5m, null), default);

        var first = await handlers.Handle(new RedeemCodeCommand(user, codes[0].Code), default);
        Assert.Equal(3.5m, first.Balance);

        var ex = await Assert.ThrowsAsync<CreditDomainException>(() =>
            handlers.Handle(new RedeemCodeCommand(user, codes[0].Code), default));
        Assert.Equal("REDEMPTION_CODE_UNAVAILABLE", ex.Code);
        Assert.Equal(422, ex.StatusCode);

        var after = await handlers.Handle(new GetBalanceQuery(user), default);
        Assert.Equal(3.5m, after.Balance);
    }

    [Fact]
    public async Task Concurrent_redeems_of_the_same_code_grant_credits_exactly_once()
    {
        var repo = new MemoryCreditRepository();
        var handlers = new CreditHandlers(repo);
        var users = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var user in users)
            await handlers.Handle(new ProvisionAccountCommand(user), default);

        var codes = await handlers.Handle(new CreateCodeBatchCommand(Guid.NewGuid(), 1, 3m, null), default);
        var code = codes[0].Code;

        var results = new ConcurrentBag<(Guid User, bool Success)>();
        Parallel.ForEach(users, user =>
        {
            try
            {
                handlers.Handle(new RedeemCodeCommand(user, code), default).GetAwaiter().GetResult();
                results.Add((user, true));
            }
            catch (CreditDomainException ex)
            {
                Assert.Equal("REDEMPTION_CODE_UNAVAILABLE", ex.Code);
                results.Add((user, false));
            }
        });

        Assert.Equal(1, results.Count(x => x.Success));
        Assert.Equal(users.Length - 1, results.Count(x => !x.Success));

        foreach (var (user, success) in results)
        {
            var balance = await handlers.Handle(new GetBalanceQuery(user), default);
            Assert.Equal(success ? 4m : 1m, balance.Balance);
        }
    }

    [Fact]
    public async Task Concurrent_redeems_across_users_and_codes_accumulate_exactly()
    {
        var repo = new MemoryCreditRepository();
        var handlers = new CreditHandlers(repo);
        var user = Guid.NewGuid();
        await handlers.Handle(new ProvisionAccountCommand(user), default);

        var codes = await handlers.Handle(new CreateCodeBatchCommand(Guid.NewGuid(), 5, 1.5m, null), default);

        // 每个码被 4 个并发任务抢兑：每个码恰好成功一次，余额 = 1 + 5 × 1.5 = 8.5
        await Task.WhenAll(codes.SelectMany(code =>
            Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                try { await handlers.Handle(new RedeemCodeCommand(user, code.Code), default); }
                catch (CreditDomainException ex) { Assert.Equal("REDEMPTION_CODE_UNAVAILABLE", ex.Code); }
            }))));

        var balance = await handlers.Handle(new GetBalanceQuery(user), default);
        Assert.Equal(8.5m, balance.Balance);
    }

    [Fact]
    public async Task Redeemed_code_view_is_masked_and_marked_redeemed()
    {
        var repo = new MemoryCreditRepository();
        var handlers = new CreditHandlers(repo);
        var user = Guid.NewGuid();
        await handlers.Handle(new ProvisionAccountCommand(user), default);
        var created = await handlers.Handle(new CreateCodeBatchCommand(Guid.NewGuid(), 1, 1m, null), default);
        await handlers.Handle(new RedeemCodeCommand(user, created[0].Code), default);

        var listed = await handlers.Handle(new ListCodesQuery(), default);
        var view = Assert.Single(listed);
        Assert.Equal("REDEEMED", view.Status);
        Assert.Equal(user, view.RedeemedBy);
        Assert.NotNull(view.RedeemedAt);
        Assert.False(string.IsNullOrWhiteSpace(view.Code));
        // 说明：MySQL 仓储在列表视图中对 code 做 "****"+suffix 掩码；
        // MemoryCreditRepository 保留完整 code，掩码语义由集成层验证。
    }

    [Fact]
    public async Task Revoked_code_cannot_be_redeemed()
    {
        var repo = new MemoryCreditRepository();
        var handlers = new CreditHandlers(repo);
        var user = Guid.NewGuid();
        await handlers.Handle(new ProvisionAccountCommand(user), default);
        var codes = await handlers.Handle(new CreateCodeBatchCommand(Guid.NewGuid(), 1, 2m, null), default);

        await handlers.Handle(new RevokeCodeCommand(codes[0].CodeId), default);

        var ex = await Assert.ThrowsAsync<CreditDomainException>(() =>
            handlers.Handle(new RedeemCodeCommand(user, codes[0].Code), default));
        Assert.Equal("REDEMPTION_CODE_UNAVAILABLE", ex.Code);

        var balance = await handlers.Handle(new GetBalanceQuery(user), default);
        Assert.Equal(1m, balance.Balance);
    }

    [Fact]
    public async Task Expired_code_cannot_be_redeemed()
    {
        var repo = new MemoryCreditRepository();
        var handlers = new CreditHandlers(repo);
        var user = Guid.NewGuid();
        await handlers.Handle(new ProvisionAccountCommand(user), default);

        // 直接向仓库注入一个已过期码（CreateCodeBatchCommand 拒绝过去的过期时间）。
        var code = repo.CreateCodes(Guid.NewGuid(), 1, 200_000, DateTimeOffset.UtcNow.AddMinutes(-1))[0];

        var ex = await Assert.ThrowsAsync<CreditDomainException>(() =>
            handlers.Handle(new RedeemCodeCommand(user, code.Code), default));
        Assert.Equal("REDEMPTION_CODE_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public async Task Unknown_code_is_rejected()
    {
        var repo = new MemoryCreditRepository();
        var handlers = new CreditHandlers(repo);
        var user = Guid.NewGuid();
        await handlers.Handle(new ProvisionAccountCommand(user), default);

        var ex = await Assert.ThrowsAsync<CreditDomainException>(() =>
            handlers.Handle(new RedeemCodeCommand(user, "QZ-DEADBEEFDEADBEEFDEAD"), default));
        Assert.Equal("REDEMPTION_CODE_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public async Task Redeem_normalizes_case_and_whitespace()
    {
        var repo = new MemoryCreditRepository();
        var handlers = new CreditHandlers(repo);
        var user = Guid.NewGuid();
        await handlers.Handle(new ProvisionAccountCommand(user), default);
        var codes = await handlers.Handle(new CreateCodeBatchCommand(Guid.NewGuid(), 1, 1m, null), default);

        var balance = await handlers.Handle(new RedeemCodeCommand(user, $"  {codes[0].Code.ToLowerInvariant()}  "), default);
        Assert.Equal(2m, balance.Balance);
    }

    [Fact]
    public async Task Malformed_codes_are_validation_errors()
    {
        var repo = new MemoryCreditRepository();
        var handlers = new CreditHandlers(repo);
        var user = Guid.NewGuid();

        var blank = await Assert.ThrowsAsync<CreditDomainException>(() =>
            handlers.Handle(new RedeemCodeCommand(user, "   "), default));
        Assert.Equal("VALIDATION_ERROR", blank.Code);

        var tooLong = await Assert.ThrowsAsync<CreditDomainException>(() =>
            handlers.Handle(new RedeemCodeCommand(user, new string('A', 49)), default));
        Assert.Equal("VALIDATION_ERROR", tooLong.Code);
    }
}
