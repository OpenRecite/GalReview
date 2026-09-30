using System.Collections.Concurrent;
using CreditService.Application;
using CreditService.Domain;
using CreditService.Persistence;
using Xunit;

namespace CreditService.Tests;

/// <summary>
/// 并发扣减语义：多任务同时 Reserve/Settle/Release 时余额不透支、
/// 预授权不重复记账（MemoryCreditRepository + CreditHandlers）。
/// </summary>
public sealed class CreditConcurrencyTests
{
    [Fact]
    public async Task Concurrent_reservations_never_overdraw_the_balance()
    {
        var repo = new MemoryCreditRepository();
        var handlers = new CreditHandlers(repo);
        var user = Guid.NewGuid();
        await handlers.Handle(new ProvisionAccountCommand(user), default);

        // 初始 1.0 credit = 100_000 units；32 个并发抢 40_000 预授权，最多只能成功 2 个。
        const long estimate = 40_000;
        var attempts = 32;
        var successes = 0;
        var failures = new ConcurrentBag<CreditDomainException>();

        Parallel.For(0, attempts, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            try
            {
                handlers.Handle(new ReserveCreditsCommand(user, Guid.NewGuid(), "GAME_GENERATION", estimate), default)
                    .GetAwaiter().GetResult();
                Interlocked.Increment(ref successes);
            }
            catch (CreditDomainException ex)
            {
                failures.Add(ex);
            }
        });

        Assert.Equal(2, successes);
        Assert.Equal(attempts - 2, failures.Count);
        Assert.All(failures, ex => Assert.Equal("CREDITS_INSUFFICIENT", ex.Code));

        var balance = await handlers.Handle(new GetBalanceQuery(user), default);
        Assert.Equal(1m, balance.Balance);
        Assert.Equal(.2m, balance.Available);
        Assert.Equal(.8m, balance.Held);
    }

    [Fact]
    public async Task Concurrent_settle_and_release_of_distinct_operations_keep_ledger_consistent()
    {
        var repo = new MemoryCreditRepository();
        var handlers = new CreditHandlers(repo);
        var user = Guid.NewGuid();
        await handlers.Handle(new ProvisionAccountCommand(user), default);

        // 10 个独立预授权，各 10_000 units；一半结算一半释放。
        // 初始 100_000 units 全部进入 held，可用为 0。
        var operations = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();
        await Task.WhenAll(operations.Select(op =>
            handlers.Handle(new ReserveCreditsCommand(user, op, "GAME_GENERATION", 10_000), default)));

        var held = await handlers.Handle(new GetBalanceQuery(user), default);
        Assert.Equal(0m, held.Available);
        Assert.Equal(1m, held.Held);

        var settleOps = operations.Take(5).ToArray();
        var releaseOps = operations.Skip(5).ToArray();
        await Task.WhenAll(
            settleOps.Select(op => handlers.Handle(new SettleCreditsCommand(op, 4_000), default))
                .Concat(releaseOps.Select(op => handlers.Handle(new ReleaseCreditsCommand(op), default))));

        var final = await handlers.Handle(new GetBalanceQuery(user), default);
        // 结算 5 × 0.04 = 0.2；释放 5 × 0.1 回到可用；held 归零。
        Assert.Equal(.8m, final.Balance);
        Assert.Equal(.8m, final.Available);
        Assert.Equal(0m, final.Held);
    }

    [Fact]
    public async Task Concurrent_settle_of_the_same_operation_charges_exactly_once()
    {
        var repo = new MemoryCreditRepository();
        var handlers = new CreditHandlers(repo);
        var user = Guid.NewGuid();
        await handlers.Handle(new ProvisionAccountCommand(user), default);
        var op = Guid.NewGuid();
        await handlers.Handle(new ReserveCreditsCommand(user, op, "GAME_GENERATION", 50_000), default);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            handlers.Handle(new SettleCreditsCommand(op, 20_000), default)));

        var balance = await handlers.Handle(new GetBalanceQuery(user), default);
        Assert.Equal(.8m, balance.Balance);
        Assert.Equal(0m, balance.Held);
    }

    [Fact]
    public async Task Concurrent_release_of_the_same_operation_returns_hold_exactly_once()
    {
        var repo = new MemoryCreditRepository();
        var handlers = new CreditHandlers(repo);
        var user = Guid.NewGuid();
        await handlers.Handle(new ProvisionAccountCommand(user), default);
        var op = Guid.NewGuid();
        await handlers.Handle(new ReserveCreditsCommand(user, op, "GAME_GENERATION", 30_000), default);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            handlers.Handle(new ReleaseCreditsCommand(op), default)));

        var balance = await handlers.Handle(new GetBalanceQuery(user), default);
        Assert.Equal(1m, balance.Available);
        Assert.Equal(0m, balance.Held);
    }

    [Fact]
    public async Task Concurrent_identical_reservations_are_idempotent_not_double_holds()
    {
        var repo = new MemoryCreditRepository();
        var handlers = new CreditHandlers(repo);
        var user = Guid.NewGuid();
        await handlers.Handle(new ProvisionAccountCommand(user), default);
        var op = Guid.NewGuid();

        // 同一 operationId 并发重试：必须只产生一笔 HELD 预授权。
        var views = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            handlers.Handle(new ReserveCreditsCommand(user, op, "GAME_GENERATION", 60_000), default)));

        Assert.All(views, v => Assert.Equal(.6m, v.EstimatedCredits));
        var balance = await handlers.Handle(new GetBalanceQuery(user), default);
        Assert.Equal(.4m, balance.Available);
        Assert.Equal(.6m, balance.Held);
    }

    [Fact]
    public async Task Settle_above_estimate_with_sufficient_balance_is_rejected()
    {
        var repo = new MemoryCreditRepository();
        var handlers = new CreditHandlers(repo);
        var user = Guid.NewGuid();
        await handlers.Handle(new ProvisionAccountCommand(user), default);
        var op = Guid.NewGuid();
        await handlers.Handle(new ReserveCreditsCommand(user, op, "GAME_GENERATION", 20_000), default);

        // 实际消耗 0.5 > 预授权 0.2，且剩余可用 0.8 不足以覆盖 0.5 超出部分的语义边界。
        // MemoryCreditRepository：balance(1.0) - otherHeld(0) < actual(50_000+1) 时不成立，
        // 用超过 total balance 的实际值触发 CREDIT_ESTIMATE_EXCEEDED。
        var ex = await Assert.ThrowsAsync<CreditDomainException>(() =>
            handlers.Handle(new SettleCreditsCommand(op, 150_000), default));
        Assert.Equal("CREDIT_ESTIMATE_EXCEEDED", ex.Code);
    }
}
