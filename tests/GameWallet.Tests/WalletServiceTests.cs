using GameWallet.Api.Admin;
using GameWallet.Api.Concurrency;
using GameWallet.Api.Domain;
using GameWallet.Api.Wallet;

namespace GameWallet.Tests;

/// <summary>A wallet with one EUR player holding 100.00.</summary>
public sealed class TestWallet
{
    public const string Provider = "provider-a";
    public const string PlayerId = "player-1";

    public TestWallet(long openingBalance = 10_000)
    {
        Store = new InMemoryWalletStore();
        var locks = new KeyedLock();
        Service = new WalletService(Store, locks, TimeProvider.System);
        Admin = new PlayerAdmin(Store, locks);
        Admin.Create(new CreatePlayerRequest(PlayerId, "EUR", openingBalance));
    }

    public InMemoryWalletStore Store { get; }
    public WalletService Service { get; }
    public PlayerAdmin Admin { get; }

    public long Balance => Store.FindPlayer(PlayerId)!.Balance;

    public Task<WalletResponse> Bet(string tx, long amount, string round = "round-1", string player = PlayerId, string currency = "EUR") =>
        Service.BetAsync(Provider, new BetRequest(tx, player, round, "slot-fruits", amount, currency));

    public Task<WalletResponse> Win(string tx, long amount, string round = "round-1", bool closed = true) =>
        Service.WinAsync(Provider, new WinRequest(tx, PlayerId, round, amount, "EUR", closed));

    public Task<WalletResponse> Rollback(string tx, string betTx, string round = "round-1") =>
        Service.RollbackAsync(Provider, new RollbackRequest(tx, PlayerId, round, betTx));
}

public class WalletServiceTests
{
    [Fact]
    public async Task Balance_returns_the_wallet()
    {
        var wallet = new TestWallet();

        var response = await wallet.Service.GetBalanceAsync(TestWallet.Provider, new BalanceRequest(TestWallet.PlayerId, "EUR"));

        Assert.Equal(WalletStatus.Ok, response.Status);
        Assert.Equal(10_000, response.Balance);
        Assert.Equal("EUR", response.Currency);
    }

    [Fact]
    public async Task Unknown_player_is_reported()
    {
        var wallet = new TestWallet();

        var response = await wallet.Service.GetBalanceAsync(TestWallet.Provider, new BalanceRequest("nobody", "EUR"));

        Assert.Equal(WalletStatus.PlayerNotFound, response.Status);
    }

    [Fact]
    public async Task Bet_then_win_moves_the_balance()
    {
        var wallet = new TestWallet();

        var bet = await wallet.Bet("tx-1", 250);
        var win = await wallet.Win("tx-2", 1000);

        Assert.Equal(WalletStatus.Ok, bet.Status);
        Assert.Equal(9_750, bet.Balance);
        Assert.Equal(WalletStatus.Ok, win.Status);
        Assert.Equal(10_750, win.Balance);
        Assert.Equal(10_750, wallet.Balance);
    }

    [Fact]
    public async Task Bet_larger_than_balance_is_refused_and_not_stored()
    {
        var wallet = new TestWallet(openingBalance: 100);

        var refused = await wallet.Bet("tx-1", 250);

        Assert.Equal(WalletStatus.InsufficientFunds, refused.Status);
        Assert.Equal(100, refused.Balance);
        Assert.Null(wallet.Store.FindTransaction(TestWallet.Provider, "tx-1"));
    }

    [Fact]
    public async Task Retried_bet_is_charged_once_and_gets_the_original_response()
    {
        var wallet = new TestWallet();

        var first = await wallet.Bet("tx-1", 250);
        var retry = await wallet.Bet("tx-1", 250);

        Assert.Equal(first, retry);
        Assert.Equal(9_750, wallet.Balance);
        Assert.Single(wallet.Store.Ledger(TestWallet.PlayerId));
    }

    [Fact]
    public async Task Reusing_a_transaction_id_for_a_different_bet_is_a_conflict()
    {
        var wallet = new TestWallet();
        await wallet.Bet("tx-1", 250);

        var conflict = await wallet.Bet("tx-1", 999);

        Assert.Equal(WalletStatus.TransactionConflict, conflict.Status);
        Assert.Equal(9_750, wallet.Balance);
    }

    [Fact]
    public async Task Retried_win_is_paid_once()
    {
        var wallet = new TestWallet();
        await wallet.Bet("tx-1", 250);

        await wallet.Win("tx-2", 1000, closed: false);
        await wallet.Win("tx-2", 1000, closed: false);

        Assert.Equal(10_750, wallet.Balance);
    }

    [Fact]
    public async Task Currency_must_match_the_wallet()
    {
        var wallet = new TestWallet();

        var response = await wallet.Bet("tx-1", 250, currency: "USD");

        Assert.Equal(WalletStatus.CurrencyMismatch, response.Status);
        Assert.Equal(10_000, wallet.Balance);
    }

    [Theory]
    [InlineData("", 100)]
    [InlineData("tx-1", -1)]
    public async Task Malformed_bets_are_rejected(string tx, long amount)
    {
        var wallet = new TestWallet();

        Assert.Equal(WalletStatus.InvalidRequest, (await wallet.Bet(tx, amount)).Status);
    }

    [Fact]
    public async Task Blocked_player_cannot_bet_but_still_gets_paid()
    {
        var wallet = new TestWallet();
        await wallet.Bet("tx-1", 250);
        await wallet.Admin.SetBlockedAsync(TestWallet.PlayerId, true, CancellationToken.None);

        var bet = await wallet.Bet("tx-2", 100, round: "round-2");
        var win = await wallet.Win("tx-3", 500);

        Assert.Equal(WalletStatus.PlayerBlocked, bet.Status);
        Assert.Equal(WalletStatus.Ok, win.Status);
        Assert.Equal(10_250, wallet.Balance);
    }

    [Fact]
    public async Task Win_needs_a_bet_in_the_round()
    {
        var wallet = new TestWallet();

        var response = await wallet.Win("tx-1", 500, round: "no-such-round");

        Assert.Equal(WalletStatus.RoundNotFound, response.Status);
        Assert.Equal(10_000, wallet.Balance);
    }

    [Fact]
    public async Task Closed_round_refuses_more_bets_and_wins()
    {
        var wallet = new TestWallet();
        await wallet.Bet("tx-1", 250);
        await wallet.Win("tx-2", 0, closed: true);

        Assert.Equal(WalletStatus.RoundClosed, (await wallet.Bet("tx-3", 100)).Status);
        Assert.Equal(WalletStatus.RoundClosed, (await wallet.Win("tx-4", 100)).Status);
    }

    [Fact]
    public async Task Rollback_refunds_the_bet()
    {
        var wallet = new TestWallet();
        await wallet.Bet("tx-1", 250);

        var rollback = await wallet.Rollback("rb-1", "tx-1");

        Assert.Equal(WalletStatus.Ok, rollback.Status);
        Assert.Equal(10_000, rollback.Balance);
        Assert.True(wallet.Store.FindTransaction(TestWallet.Provider, "tx-1")!.RolledBack);
    }

    [Fact]
    public async Task Rollback_is_idempotent()
    {
        var wallet = new TestWallet();
        await wallet.Bet("tx-1", 250);

        await wallet.Rollback("rb-1", "tx-1");
        await wallet.Rollback("rb-1", "tx-1");   // same rollback retried
        await wallet.Rollback("rb-2", "tx-1");   // a second rollback for the same bet

        Assert.Equal(10_000, wallet.Balance);
        Assert.Equal(2, wallet.Store.Ledger(TestWallet.PlayerId).Count); // the bet and one refund
    }

    [Fact]
    public async Task Rollback_before_the_bet_arrives_blocks_the_late_bet()
    {
        var wallet = new TestWallet();

        // The provider timed out waiting for tx-1, rolled it back, and then the bet finally arrives.
        var rollback = await wallet.Rollback("rb-1", "tx-1");
        var lateBet = await wallet.Bet("tx-1", 250);

        Assert.Equal(WalletStatus.Ok, rollback.Status);
        Assert.Equal(WalletStatus.TransactionRolledBack, lateBet.Status);
        Assert.Equal(10_000, wallet.Balance);
    }

    [Fact]
    public async Task Settled_rounds_cannot_be_rolled_back()
    {
        var wallet = new TestWallet();
        await wallet.Bet("tx-1", 250);
        await wallet.Win("tx-2", 1000);

        var rollback = await wallet.Rollback("rb-1", "tx-1");

        Assert.Equal(WalletStatus.RollbackNotAllowed, rollback.Status);
        Assert.Equal(10_750, wallet.Balance);
    }

    [Fact]
    public async Task Only_bets_can_be_rolled_back()
    {
        var wallet = new TestWallet();
        await wallet.Bet("tx-1", 250);
        await wallet.Win("tx-2", 1000, closed: false);

        Assert.Equal(WalletStatus.RollbackNotAllowed, (await wallet.Rollback("rb-1", "tx-2")).Status);
    }

    [Fact]
    public async Task Win_after_every_bet_was_rolled_back_is_refused()
    {
        var wallet = new TestWallet();
        await wallet.Bet("tx-1", 250);
        await wallet.Rollback("rb-1", "tx-1");

        var win = await wallet.Win("tx-2", 1000);

        Assert.Equal(WalletStatus.TransactionRolledBack, win.Status);
        Assert.Equal(10_000, wallet.Balance);
    }

    [Fact]
    public async Task Transaction_ids_are_scoped_per_provider()
    {
        var wallet = new TestWallet();
        await wallet.Bet("tx-1", 250);

        var other = await wallet.Service.BetAsync("provider-b", new BetRequest("tx-1", TestWallet.PlayerId, "round-9", "roulette", 100, "EUR"));

        Assert.Equal(WalletStatus.Ok, other.Status);
        Assert.Equal(9_650, wallet.Balance);
    }

    [Fact]
    public async Task Ledger_records_every_change_with_the_running_balance()
    {
        var wallet = new TestWallet();
        await wallet.Bet("tx-1", 250);
        await wallet.Bet("tx-2", 100, round: "round-2");
        await wallet.Rollback("rb-1", "tx-2", round: "round-2");
        await wallet.Win("tx-3", 1000);

        var ledger = wallet.Store.Ledger(TestWallet.PlayerId);

        Assert.Equal(new long[] { -250, -100, 100, 1000 }, ledger.Select(e => e.Amount).ToArray());
        Assert.Equal(new long[] { 9_750, 9_650, 9_750, 10_750 }, ledger.Select(e => e.BalanceAfter).ToArray());
        Assert.True((await wallet.Admin.ReconcileAsync(CancellationToken.None)).Single().Matches);
    }
}
