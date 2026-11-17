using GameWallet.Api.Domain;
using GameWallet.Api.Security;

namespace GameWallet.Api.Wallet;

/// <summary>The endpoints a game provider calls. Every call is signed (see <see cref="ProviderAuthMiddleware"/>).</summary>
public static class WalletEndpoints
{
    public static void MapWalletEndpoints(this IEndpointRouteBuilder app)
    {
        var wallet = app.MapGroup("/wallet");

        wallet.MapPost("/balance", (BalanceRequest request, HttpContext http, WalletService service, CancellationToken ct) =>
            Respond(service.GetBalanceAsync(Provider(http), request, ct)));

        wallet.MapPost("/bet", (BetRequest request, HttpContext http, WalletService service, CancellationToken ct) =>
            Respond(service.BetAsync(Provider(http), request, ct)));

        wallet.MapPost("/win", (WinRequest request, HttpContext http, WalletService service, CancellationToken ct) =>
            Respond(service.WinAsync(Provider(http), request, ct)));

        wallet.MapPost("/rollback", (RollbackRequest request, HttpContext http, WalletService service, CancellationToken ct) =>
            Respond(service.RollbackAsync(Provider(http), request, ct)));
    }

    private static string Provider(HttpContext http) => (string)http.Items[ProviderAuthMiddleware.ProviderItem]!;

    private static async Task<IResult> Respond(Task<WalletResponse> call)
    {
        var response = await call;
        return Results.Json(response, statusCode: HttpStatusFor(response.Status));
    }

    public static int HttpStatusFor(WalletStatus status) => status switch
    {
        WalletStatus.Ok => StatusCodes.Status200OK,
        WalletStatus.InvalidRequest => StatusCodes.Status400BadRequest,
        WalletStatus.PlayerBlocked => StatusCodes.Status403Forbidden,
        WalletStatus.PlayerNotFound or WalletStatus.RoundNotFound => StatusCodes.Status404NotFound,
        WalletStatus.TransactionConflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status422UnprocessableEntity
    };
}
