using System.Collections.Concurrent;
using GameWallet.Api.Domain;

namespace GameWallet.Api.Wallet;

/// <summary>
/// Storage for players, transactions, rounds and the ledger.
/// In production this is a SQL database: the transaction table has a unique index on
/// (provider_id, transaction_id) and each wallet call runs in one database transaction.
/// </summary>
public interface IWalletStore
{
    Player? FindPlayer(string playerId);
    bool TryAddPlayer(Player player);
    IReadOnlyCollection<Player> AllPlayers();

    TransactionRecord? FindTransaction(string providerId, string transactionId);

    /// <summary>Atomic claim of a transaction ID; false if it already exists.</summary>
    bool TryAddTransaction(TransactionRecord record);

    Round? FindRound(string providerId, string roundId);
    void AddRound(Round round);

    void AppendLedger(string playerId, TransactionType type, string providerId, string transactionId, string roundId, long amount, long balanceAfter, DateTimeOffset at);
    IReadOnlyList<LedgerEntry> Ledger(string playerId);
}

public sealed class InMemoryWalletStore : IWalletStore
{
    private readonly ConcurrentDictionary<string, Player> _players = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string, string), TransactionRecord> _transactions = new();
    private readonly ConcurrentDictionary<(string, string), Round> _rounds = new();
    private readonly ConcurrentDictionary<string, List<LedgerEntry>> _ledger = new(StringComparer.Ordinal);
    private long _sequence;

    public Player? FindPlayer(string playerId) => _players.GetValueOrDefault(playerId);

    public bool TryAddPlayer(Player player) => _players.TryAdd(player.Id, player);

    public IReadOnlyCollection<Player> AllPlayers() => [.. _players.Values];

    public TransactionRecord? FindTransaction(string providerId, string transactionId) =>
        _transactions.GetValueOrDefault((providerId, transactionId));

    public bool TryAddTransaction(TransactionRecord record) =>
        _transactions.TryAdd((record.ProviderId, record.TransactionId), record);

    public Round? FindRound(string providerId, string roundId) => _rounds.GetValueOrDefault((providerId, roundId));

    public void AddRound(Round round) => _rounds[(round.ProviderId, round.RoundId)] = round;

    public void AppendLedger(string playerId, TransactionType type, string providerId, string transactionId, string roundId, long amount, long balanceAfter, DateTimeOffset at)
    {
        var entries = _ledger.GetOrAdd(playerId, _ => []);
        lock (entries)
            entries.Add(new LedgerEntry(Interlocked.Increment(ref _sequence), playerId, type, providerId, transactionId, roundId, amount, balanceAfter, at));
    }

    public IReadOnlyList<LedgerEntry> Ledger(string playerId)
    {
        if (!_ledger.TryGetValue(playerId, out var entries))
            return [];
        lock (entries)
            return [.. entries];
    }
}
