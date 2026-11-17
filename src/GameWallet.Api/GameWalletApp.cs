using System.Text.Json;
using System.Text.Json.Serialization;
using GameWallet.Api.Admin;
using GameWallet.Api.Concurrency;
using GameWallet.Api.Security;
using GameWallet.Api.Wallet;

namespace GameWallet.Api;

public static class GameWalletApp
{
    /// <param name="configure">Lets tests replace services or settings before the app is built.</param>
    public static WebApplication Create(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Status codes go out as INSUFFICIENT_FUNDS, TRANSACTION_CONFLICT, ... like most provider APIs.
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });
        builder.Services.AddProblemDetails();
        builder.Services.AddHealthChecks();

        builder.Services.Configure<ProviderOptions>(builder.Configuration.GetSection("Providers"));
        builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("Admin"));

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<KeyedLock>();
        builder.Services.AddSingleton<IWalletStore, InMemoryWalletStore>();
        builder.Services.AddSingleton<WalletService>();
        builder.Services.AddSingleton<PlayerAdmin>();

        configure?.Invoke(builder);

        var app = builder.Build();

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseMiddleware<AdminApiKeyMiddleware>();
        app.UseMiddleware<ProviderAuthMiddleware>();

        app.MapHealthChecks("/health");
        app.MapWalletEndpoints();
        app.MapAdminEndpoints();

        SeedPlayers(app);
        return app;
    }

    /// <summary>Creates the demo players listed under "SeedPlayers" in configuration.</summary>
    private static void SeedPlayers(WebApplication app)
    {
        var admin = app.Services.GetRequiredService<PlayerAdmin>();
        foreach (var player in app.Configuration.GetSection("SeedPlayers").Get<List<CreatePlayerRequest>>() ?? [])
            admin.Create(player);
    }
}
