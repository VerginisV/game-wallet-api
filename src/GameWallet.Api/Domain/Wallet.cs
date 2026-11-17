namespace GameWallet.Api.Domain;

/// <summary>Result codes returned to the game provider. Serialized as UPPER_SNAKE_CASE.</summary>
public enum WalletStatus
{
    Ok,
    InvalidRequest,
    PlayerNotFound,
    PlayerBlocked,
    CurrencyMismatch,
    InsufficientFunds,
    TransactionConflict,
    TransactionRolledBack,
    RoundNotFound,
    RoundClosed,
    RollbackNotAllowed
}

public enum TransactionType
{
    Bet,
    Win,
    Rollback
}

/// <summary>
/// A player's wallet. All amounts are in minor units (cents) so there is no floating point anywhere.
/// The balance is only changed by <see cref="Wallet.WalletService"/> while holding the player's lock.
/// </summary>
public sealed class Player(string id, string currency, long openingBalance)
{
    public string Id { get; } = id;
    public string Currency { get; } = currency;
    public long OpeningBalance { get; } = openingBalance;
    public long Balance { get; internal set; } = openingBalance;
    public bool Blocked { get; internal set; }
}

/// <summary>
/// One processed provider transaction. Kept so retries can be answered with the original result,
/// and so rollbacks can find the bet they cancel.
/// </summary>
public sealed class TransactionRecord
{
    public required string ProviderId { get; init; }
    public required string TransactionId { get; init; }
    public required TransactionType Type { get; init; }
    public required string PlayerId { get; init; }
    public required string RoundId { get; init; }
    public required long Amount { get; init; }
    public required string Fingerprint { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? ReferenceTransactionId { get; init; }
    public WalletResponse? Response { get; set; }

    /// <summary>The bet was cancelled by a rollback.</summary>
    public bool RolledBack { get; set; }

    /// <summary>
    /// Placeholder for a bet we never saw: the provider rolled it back first (it timed out on their side).
    /// If the original bet arrives late, it must be refused instead of taking the player's money.
    /// </summary>
    public bool Tombstone { get; init; }
}

/// <summary>A game round groups the bets and wins of one spin or hand.</summary>
public sealed class Round(string providerId, string roundId, string playerId, string gameId)
{
    public string ProviderId { get; } = providerId;
    public string RoundId { get; } = roundId;
    public string PlayerId { get; } = playerId;
    public string GameId { get; } = gameId;
    public long TotalBet { get; internal set; }
    public long TotalWin { get; internal set; }

    /// <summary>Bets in this round that have not been rolled back.</summary>
    public int ActiveBets { get; internal set; }
    public bool Closed { get; internal set; }
}

/// <summary>Append-only record of every balance change; the balance can always be rebuilt from it.</summary>
public sealed record LedgerEntry(
    long Sequence,
    string PlayerId,
    TransactionType Type,
    string ProviderId,
    string TransactionId,
    string RoundId,
    long Amount,
    long BalanceAfter,
    DateTimeOffset CreatedAt);

/// <summary>What the provider receives for every wallet call.</summary>
public sealed record WalletResponse(
    WalletStatus Status,
    long Balance,
    string Currency,
    string? TransactionId = null,
    string? Message = null);
