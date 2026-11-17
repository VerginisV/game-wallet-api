using GameWallet.Api.Concurrency;
using GameWallet.Api.Domain;

namespace GameWallet.Api.Wallet;

/// <summary>
/// The seamless wallet: the game provider holds no money, it calls us for every bet and win.
///
/// Rules that keep the money right:
/// - Every balance change for a player runs under that player's lock.
/// - Transaction IDs are unique per provider. A retry with the same ID and the same content gets the
///   original response and moves no money; the same ID with different content is a conflict.
/// - A rollback of a bet we never saw leaves a tombstone, so the late bet is refused, not charged.
/// - Failed calls (insufficient funds, blocked player) are not stored, so a retry is judged again.
/// </summary>
public sealed class WalletService(IWalletStore store, KeyedLock locks, TimeProvider time)
{
    public async Task<WalletResponse> GetBalanceAsync(string providerId, BalanceRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.PlayerId))
            return Fail(WalletStatus.InvalidRequest, "playerId is required", null, request.Currency);

        using var _ = await locks.AcquireAsync(request.PlayerId, ct);

        var player = store.FindPlayer(request.PlayerId);
        if (player is null)
            return Fail(WalletStatus.PlayerNotFound, $"Unknown player '{request.PlayerId}'", null, request.Currency);
        if (!SameCurrency(player, request.Currency))
            return Fail(WalletStatus.CurrencyMismatch, $"Player wallet is in {player.Currency}", player);

        return new WalletResponse(WalletStatus.Ok, player.Balance, player.Currency);
    }

    public async Task<WalletResponse> BetAsync(string providerId, BetRequest request, CancellationToken ct = default)
    {
        var invalid = Require(("transactionId", request.TransactionId), ("playerId", request.PlayerId), ("roundId", request.RoundId), ("gameId", request.GameId))
                      ?? (request.Amount < 0 ? "amount cannot be negative" : null);
        if (invalid is not null)
            return Fail(WalletStatus.InvalidRequest, invalid, null, request.Currency);

        var fingerprint = Fingerprint(TransactionType.Bet, request.PlayerId, request.RoundId, request.Amount, request.Currency, request.GameId);

        using var _ = await locks.AcquireAsync(request.PlayerId, ct);

        var player = store.FindPlayer(request.PlayerId);
        if (player is null)
            return Fail(WalletStatus.PlayerNotFound, $"Unknown player '{request.PlayerId}'", null, request.Currency);

        if (store.FindTransaction(providerId, request.TransactionId) is { } existing)
            return Replay(existing, fingerprint, player);

        if (player.Blocked)
            return Fail(WalletStatus.PlayerBlocked, "Player is blocked from betting", player);
        if (!SameCurrency(player, request.Currency))
            return Fail(WalletStatus.CurrencyMismatch, $"Player wallet is in {player.Currency}", player);

        var round = store.FindRound(providerId, request.RoundId);
        if (round is not null && round.PlayerId != player.Id)
            return Fail(WalletStatus.InvalidRequest, "Round belongs to another player", player);
        if (round is { Closed: true })
            return Fail(WalletStatus.RoundClosed, "Round is already closed", player);

        if (player.Balance < request.Amount)
            return Fail(WalletStatus.InsufficientFunds, "Insufficient funds", player);

        var record = NewRecord(providerId, request.TransactionId, TransactionType.Bet, player.Id, request.RoundId, request.Amount, fingerprint);
        if (!store.TryAddTransaction(record))
            return Replay(store.FindTransaction(providerId, request.TransactionId)!, fingerprint, player);

        if (round is null)
        {
            round = new Round(providerId, request.RoundId, player.Id, request.GameId);
            store.AddRound(round);
        }

        round.TotalBet += request.Amount;
        round.ActiveBets++;
        return Apply(player, record, -request.Amount);
    }

    public async Task<WalletResponse> WinAsync(string providerId, WinRequest request, CancellationToken ct = default)
    {
        var invalid = Require(("transactionId", request.TransactionId), ("playerId", request.PlayerId), ("roundId", request.RoundId))
                      ?? (request.Amount < 0 ? "amount cannot be negative" : null);
        if (invalid is not null)
            return Fail(WalletStatus.InvalidRequest, invalid, null, request.Currency);

        var fingerprint = Fingerprint(TransactionType.Win, request.PlayerId, request.RoundId, request.Amount, request.Currency, request.RoundClosed.ToString());

        using var _ = await locks.AcquireAsync(request.PlayerId, ct);

        var player = store.FindPlayer(request.PlayerId);
        if (player is null)
            return Fail(WalletStatus.PlayerNotFound, $"Unknown player '{request.PlayerId}'", null, request.Currency);

        if (store.FindTransaction(providerId, request.TransactionId) is { } existing)
            return Replay(existing, fingerprint, player);

        // Blocked players still get paid: the bet was accepted before the block.
        if (!SameCurrency(player, request.Currency))
            return Fail(WalletStatus.CurrencyMismatch, $"Player wallet is in {player.Currency}", player);

        var round = store.FindRound(providerId, request.RoundId);
        if (round is null || round.PlayerId != player.Id)
            return Fail(WalletStatus.RoundNotFound, "No bet was placed in this round", player);
        if (round.Closed)
            return Fail(WalletStatus.RoundClosed, "Round is already closed", player);
        if (round.ActiveBets == 0)
            return Fail(WalletStatus.TransactionRolledBack, "Every bet in this round was rolled back", player);

        var record = NewRecord(providerId, request.TransactionId, TransactionType.Win, player.Id, request.RoundId, request.Amount, fingerprint);
        if (!store.TryAddTransaction(record))
            return Replay(store.FindTransaction(providerId, request.TransactionId)!, fingerprint, player);

        round.TotalWin += request.Amount;
        round.Closed = request.RoundClosed;
        return Apply(player, record, request.Amount);
    }

    public async Task<WalletResponse> RollbackAsync(string providerId, RollbackRequest request, CancellationToken ct = default)
    {
        var invalid = Require(("transactionId", request.TransactionId), ("playerId", request.PlayerId), ("roundId", request.RoundId), ("referenceTransactionId", request.ReferenceTransactionId));
        if (invalid is not null)
            return Fail(WalletStatus.InvalidRequest, invalid, null, "");

        var fingerprint = Fingerprint(TransactionType.Rollback, request.PlayerId, request.RoundId, 0, "", request.ReferenceTransactionId);

        using var _ = await locks.AcquireAsync(request.PlayerId, ct);

        var player = store.FindPlayer(request.PlayerId);
        if (player is null)
            return Fail(WalletStatus.PlayerNotFound, $"Unknown player '{request.PlayerId}'", null, "");

        if (store.FindTransaction(providerId, request.TransactionId) is { } existing)
            return Replay(existing, fingerprint, player);

        var bet = store.FindTransaction(providerId, request.ReferenceTransactionId);

        if (bet is null)
        {
            // The provider gave up on a bet that never reached us (or is still on its way).
            // Leave a tombstone so it is refused if it arrives late, and answer OK: no money moved.
            var tombstone = new TransactionRecord
            {
                ProviderId = providerId,
                TransactionId = request.ReferenceTransactionId,
                Type = TransactionType.Bet,
                PlayerId = player.Id,
                RoundId = request.RoundId,
                Amount = 0,
                Fingerprint = "tombstone",
                CreatedAt = time.GetUtcNow(),
                Tombstone = true
            };
            if (!store.TryAddTransaction(tombstone))
                bet = store.FindTransaction(providerId, request.ReferenceTransactionId)!;
            else
                return Record(providerId, player, request, fingerprint, "Bet not found; it will be refused if it arrives later");
        }

        if (bet.Tombstone || bet.RolledBack)
            return Record(providerId, player, request, fingerprint, "Bet was already rolled back");
        if (bet.Type != TransactionType.Bet)
            return Fail(WalletStatus.RollbackNotAllowed, "Only bets can be rolled back", player);
        if (bet.PlayerId != player.Id || bet.RoundId != request.RoundId)
            return Fail(WalletStatus.RollbackNotAllowed, "Bet belongs to another player or round", player);

        var round = store.FindRound(providerId, bet.RoundId)!;
        if (round.Closed || round.TotalWin > 0)
            return Fail(WalletStatus.RollbackNotAllowed, "Round is already settled", player);

        var record = NewRecord(providerId, request.TransactionId, TransactionType.Rollback, player.Id, request.RoundId, bet.Amount, fingerprint, request.ReferenceTransactionId);
        if (!store.TryAddTransaction(record))
            return Replay(store.FindTransaction(providerId, request.TransactionId)!, fingerprint, player);

        bet.RolledBack = true;
        round.TotalBet -= bet.Amount;
        round.ActiveBets--;
        return Apply(player, record, bet.Amount);
    }

    /// <summary>Stores a rollback that moved no money, so its retries are answered the same way.</summary>
    private WalletResponse Record(string providerId, Player player, RollbackRequest request, string fingerprint, string message)
    {
        var record = NewRecord(providerId, request.TransactionId, TransactionType.Rollback, player.Id, request.RoundId, 0, fingerprint, request.ReferenceTransactionId);
        if (!store.TryAddTransaction(record))
            return Replay(store.FindTransaction(providerId, request.TransactionId)!, fingerprint, player);
        return Ok(player, record, message);
    }

    private WalletResponse Ok(Player player, TransactionRecord record, string? message) =>
        record.Response = new WalletResponse(WalletStatus.Ok, player.Balance, player.Currency, record.TransactionId, message);

    private WalletResponse Apply(Player player, TransactionRecord record, long delta)
    {
        player.Balance += delta;
        store.AppendLedger(player.Id, record.Type, record.ProviderId, record.TransactionId, record.RoundId, delta, player.Balance, record.CreatedAt);
        return Ok(player, record, null);
    }

    private static WalletResponse Replay(TransactionRecord existing, string fingerprint, Player player)
    {
        if (existing.Tombstone)
            return Fail(WalletStatus.TransactionRolledBack, "This transaction was rolled back before it arrived", player);
        if (existing.Fingerprint != fingerprint || existing.Response is null)
            return Fail(WalletStatus.TransactionConflict, "Transaction ID was already used for a different request", player);
        return existing.Response;
    }

    private TransactionRecord NewRecord(string providerId, string transactionId, TransactionType type, string playerId, string roundId, long amount, string fingerprint, string? reference = null) => new()
    {
        ProviderId = providerId,
        TransactionId = transactionId,
        Type = type,
        PlayerId = playerId,
        RoundId = roundId,
        Amount = amount,
        Fingerprint = fingerprint,
        CreatedAt = time.GetUtcNow(),
        ReferenceTransactionId = reference
    };

    private static WalletResponse Fail(WalletStatus status, string message, Player? player, string? currency = null) =>
        new(status, player?.Balance ?? 0, player?.Currency ?? currency ?? "", Message: message);

    private static bool SameCurrency(Player player, string? currency) =>
        string.Equals(player.Currency, currency, StringComparison.OrdinalIgnoreCase);

    private static string Fingerprint(TransactionType type, string playerId, string roundId, long amount, string? currency, string? extra) =>
        string.Join('|', type, playerId, roundId, amount, currency?.ToUpperInvariant(), extra);

    private static string? Require(params (string Name, string? Value)[] fields) =>
        fields.Where(f => string.IsNullOrWhiteSpace(f.Value)).Select(f => $"{f.Name} is required").FirstOrDefault();
}
