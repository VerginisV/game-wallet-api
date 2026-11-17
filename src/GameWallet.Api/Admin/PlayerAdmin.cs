using GameWallet.Api.Concurrency;
using GameWallet.Api.Domain;
using GameWallet.Api.Wallet;

namespace GameWallet.Api.Admin;

public sealed record CreatePlayerRequest(string PlayerId, string Currency, long OpeningBalance);

public sealed record PlayerView(string PlayerId, string Currency, long Balance, bool Blocked);

public sealed record ReconciliationLine(string PlayerId, long OpeningBalance, long LedgerTotal, long Balance, bool Matches);

/// <summary>Back-office operations: create and block players, read the ledger, reconcile balances.</summary>
public sealed class PlayerAdmin(IWalletStore store, KeyedLock locks)
{
    public PlayerView? Create(CreatePlayerRequest request)
    {
        var player = new Player(request.PlayerId, request.Currency.ToUpperInvariant(), request.OpeningBalance);
        return store.TryAddPlayer(player) ? View(player) : null;
    }

    public PlayerView? Get(string playerId) => store.FindPlayer(playerId) is { } p ? View(p) : null;

    public async Task<PlayerView?> SetBlockedAsync(string playerId, bool blocked, CancellationToken ct)
    {
        using var _ = await locks.AcquireAsync(playerId, ct);
        var player = store.FindPlayer(playerId);
        if (player is null)
            return null;
        player.Blocked = blocked;
        return View(player);
    }

    public IReadOnlyList<LedgerEntry>? Ledger(string playerId) =>
        store.FindPlayer(playerId) is null ? null : store.Ledger(playerId);

    /// <summary>
    /// Proves no money was created or lost: for every player,
    /// opening balance + sum of ledger entries must equal the current balance.
    /// </summary>
    public async Task<IReadOnlyList<ReconciliationLine>> ReconcileAsync(CancellationToken ct)
    {
        var lines = new List<ReconciliationLine>();
        foreach (var player in store.AllPlayers().OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            using var _ = await locks.AcquireAsync(player.Id, ct);
            var total = store.Ledger(player.Id).Sum(e => e.Amount);
            lines.Add(new ReconciliationLine(player.Id, player.OpeningBalance, total, player.Balance, player.OpeningBalance + total == player.Balance));
        }
        return lines;
    }

    private static PlayerView View(Player p) => new(p.Id, p.Currency, p.Balance, p.Blocked);
}

public static class AdminEndpoints
{
    public sealed record BlockRequest(bool Blocked);

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin");

        admin.MapPost("/players", (CreatePlayerRequest request, PlayerAdmin players) =>
        {
            if (string.IsNullOrWhiteSpace(request.PlayerId) || request.Currency?.Length != 3 || request.OpeningBalance < 0)
                return Results.Problem("playerId, a 3-letter currency and a non-negative openingBalance are required", statusCode: 400);
            return players.Create(request) is { } view
                ? Results.Created($"/admin/players/{view.PlayerId}", view)
                : Results.Problem("Player already exists", statusCode: 409);
        });

        admin.MapGet("/players/{playerId}", (string playerId, PlayerAdmin players) =>
            players.Get(playerId) is { } view ? Results.Ok(view) : Results.NotFound());

        admin.MapPost("/players/{playerId}/block", async (string playerId, BlockRequest request, PlayerAdmin players, CancellationToken ct) =>
            await players.SetBlockedAsync(playerId, request.Blocked, ct) is { } view ? Results.Ok(view) : Results.NotFound());

        admin.MapGet("/players/{playerId}/ledger", (string playerId, PlayerAdmin players) =>
            players.Ledger(playerId) is { } ledger ? Results.Ok(ledger) : Results.NotFound());

        admin.MapGet("/reconciliation", async (PlayerAdmin players, CancellationToken ct) =>
        {
            var lines = await players.ReconcileAsync(ct);
            return Results.Ok(new { allMatch = lines.All(l => l.Matches), players = lines });
        });
    }
}
