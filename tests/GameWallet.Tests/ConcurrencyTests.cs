using GameWallet.Api.Domain;

namespace GameWallet.Tests;

/// <summary>Many calls at once, the way a busy provider sends them.</summary>
public class ConcurrencyTests
{
    [Fact]
    public async Task Parallel_bets_never_overdraw_the_wallet()
    {
        var wallet = new TestWallet(openingBalance: 1_000);

        // 50 bets of 100 against a balance of 1000: exactly 10 may succeed.
        var results = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(i => Task.Run(() => wallet.Bet($"tx-{i}", 100, round: $"round-{i}"))));

        Assert.Equal(10, results.Count(r => r.Status == WalletStatus.Ok));
        Assert.Equal(40, results.Count(r => r.Status == WalletStatus.InsufficientFunds));
        Assert.Equal(0, wallet.Balance);
    }

    [Fact]
    public async Task The_same_bet_sent_many_times_at_once_is_charged_once()
    {
        var wallet = new TestWallet();

        var results = await Task.WhenAll(Enumerable.Range(0, 30)
            .Select(_ => Task.Run(() => wallet.Bet("tx-1", 250))));

        Assert.All(results, r => Assert.Equal(WalletStatus.Ok, r.Status));
        Assert.Equal(9_750, wallet.Balance);
        Assert.Single(wallet.Store.Ledger(TestWallet.PlayerId));
    }

    [Fact]
    public async Task Bet_and_rollback_racing_leave_a_consistent_wallet()
    {
        for (var i = 0; i < 50; i++)
        {
            var wallet = new TestWallet();

            var bet = Task.Run(() => wallet.Bet("tx-1", 250));
            var rollback = Task.Run(() => wallet.Rollback("rb-1", "tx-1"));
            var results = await Task.WhenAll(bet, rollback);

            // Whichever arrives first, the player ends with their full balance:
            // rollback first leaves a tombstone and the bet is refused; bet first is then refunded.
            Assert.Equal(10_000, wallet.Balance);
            Assert.Equal(WalletStatus.Ok, results[1].Status);
            Assert.True((await wallet.Admin.ReconcileAsync(CancellationToken.None)).Single().Matches);
        }
    }

    [Fact]
    public async Task Mixed_traffic_reconciles()
    {
        var wallet = new TestWallet(openingBalance: 100_000);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
        {
            var round = $"round-{i}";
            await wallet.Bet($"bet-{i}", 100 + i, round);
            if (i % 3 == 0)
                await wallet.Rollback($"rb-{i}", $"bet-{i}", round);
            else
                await wallet.Win($"win-{i}", i % 2 == 0 ? 500 : 0, round);
        })));

        var line = (await wallet.Admin.ReconcileAsync(CancellationToken.None)).Single();
        Assert.True(line.Matches);
        Assert.Equal(wallet.Store.Ledger(TestWallet.PlayerId).Last().BalanceAfter, wallet.Balance);
    }
}
