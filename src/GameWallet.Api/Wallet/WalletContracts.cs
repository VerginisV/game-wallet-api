namespace GameWallet.Api.Wallet;

// Requests the game provider sends. Amounts are in minor units: 250 means 2.50 EUR.

public sealed record BalanceRequest(string PlayerId, string Currency);

public sealed record BetRequest(
    string TransactionId,
    string PlayerId,
    string RoundId,
    string GameId,
    long Amount,
    string Currency);

/// <param name="RoundClosed">True when this is the last win of the round; later bets on it are refused.</param>
public sealed record WinRequest(
    string TransactionId,
    string PlayerId,
    string RoundId,
    long Amount,
    string Currency,
    bool RoundClosed = true);

/// <param name="ReferenceTransactionId">The bet being cancelled.</param>
public sealed record RollbackRequest(
    string TransactionId,
    string PlayerId,
    string RoundId,
    string ReferenceTransactionId);
