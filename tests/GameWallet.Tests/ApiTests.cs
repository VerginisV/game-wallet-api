using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using GameWallet.Api;
using GameWallet.Api.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace GameWallet.Tests;

/// <summary>Starts the real app on a free local port and calls it the way a game provider would.</summary>
public sealed class TestApi : IAsyncDisposable
{
    public const string ProviderId = "test-provider";
    public const string Secret = "test_secret";
    public const string AdminKey = "test_admin_key";

    private readonly WebApplication _app;

    private TestApi(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    public HttpClient Client { get; }

    public static async Task<TestApi> StartAsync()
    {
        var app = GameWalletApp.Create(
        [
            "--urls=http://127.0.0.1:0",
            "--environment=Testing",
            $"--Providers:Secrets:{ProviderId}={Secret}",
            $"--Admin:ApiKeys:0={AdminKey}",
            "--SeedPlayers:0:PlayerId=player-1",
            "--SeedPlayers:0:Currency=EUR",
            "--SeedPlayers:0:OpeningBalance=10000",
            "--Logging:LogLevel:Default=Warning"
        ]);

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new TestApi(app, new HttpClient { BaseAddress = new Uri(address) });
    }

    public Task<HttpResponseMessage> SignedPostAsync(string path, object body, string? secret = null, long? timestamp = null)
    {
        var json = JsonSerializer.Serialize(body, JsonSerializerOptions.Web);
        var ts = (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(CultureInfo.InvariantCulture);
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add(ProviderAuthMiddleware.ProviderHeader, ProviderId);
        request.Headers.Add(ProviderAuthMiddleware.TimestampHeader, ts);
        request.Headers.Add(ProviderAuthMiddleware.SignatureHeader, ProviderSignature.Compute(secret ?? Secret, ts, json));
        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> AdminGetAsync(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(AdminApiKeyMiddleware.HeaderName, AdminKey);
        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public class ApiTests
{
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static object Bet(string tx, long amount) =>
        new { transactionId = tx, playerId = "player-1", roundId = "round-1", gameId = "slot-fruits", amount, currency = "EUR" };

    [Fact]
    public async Task Health_endpoint_is_public()
    {
        await using var api = await TestApi.StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await api.Client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Signed_balance_call_returns_the_wallet()
    {
        await using var api = await TestApi.StartAsync();

        var response = await api.SignedPostAsync("/wallet/balance", new { playerId = "player-1", currency = "EUR" });
        var body = await JsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("OK", body.GetProperty("status").GetString());
        Assert.Equal(10_000, body.GetProperty("balance").GetInt64());
    }

    [Fact]
    public async Task Wrong_secret_is_rejected()
    {
        await using var api = await TestApi.StartAsync();

        var response = await api.SignedPostAsync("/wallet/balance", new { playerId = "player-1", currency = "EUR" }, secret: "wrong");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("INVALID_SIGNATURE", (await JsonAsync(response)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Old_timestamps_are_rejected()
    {
        await using var api = await TestApi.StartAsync();
        var hourAgo = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();

        var response = await api.SignedPostAsync("/wallet/balance", new { playerId = "player-1", currency = "EUR" }, timestamp: hourAgo);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unsigned_calls_are_rejected()
    {
        await using var api = await TestApi.StartAsync();

        var response = await api.Client.PostAsync("/wallet/balance",
            new StringContent("""{"playerId":"player-1","currency":"EUR"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Full_round_over_http()
    {
        await using var api = await TestApi.StartAsync();

        var bet = await JsonAsync(await api.SignedPostAsync("/wallet/bet", Bet("tx-1", 250)));
        var win = await JsonAsync(await api.SignedPostAsync("/wallet/win",
            new { transactionId = "tx-2", playerId = "player-1", roundId = "round-1", amount = 1000, currency = "EUR", roundClosed = true }));

        Assert.Equal(9_750, bet.GetProperty("balance").GetInt64());
        Assert.Equal("tx-1", bet.GetProperty("transactionId").GetString());
        Assert.Equal(10_750, win.GetProperty("balance").GetInt64());
    }

    [Fact]
    public async Task Business_errors_use_upper_snake_case_codes()
    {
        await using var api = await TestApi.StartAsync();

        var response = await api.SignedPostAsync("/wallet/bet", Bet("tx-1", 1_000_000));
        var body = await JsonAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("INSUFFICIENT_FUNDS", body.GetProperty("status").GetString());
        Assert.Equal(10_000, body.GetProperty("balance").GetInt64());
    }

    [Fact]
    public async Task Transaction_conflict_returns_409()
    {
        await using var api = await TestApi.StartAsync();
        await api.SignedPostAsync("/wallet/bet", Bet("tx-1", 250));

        var response = await api.SignedPostAsync("/wallet/bet", Bet("tx-1", 300));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("TRANSACTION_CONFLICT", (await JsonAsync(response)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Admin_endpoints_require_the_api_key()
    {
        await using var api = await TestApi.StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client.GetAsync("/admin/reconciliation")).StatusCode);
    }

    [Fact]
    public async Task Ledger_and_reconciliation_are_available_to_the_back_office()
    {
        await using var api = await TestApi.StartAsync();
        await api.SignedPostAsync("/wallet/bet", Bet("tx-1", 250));

        var ledger = await JsonAsync(await api.AdminGetAsync("/admin/players/player-1/ledger"));
        var reconciliation = await JsonAsync(await api.AdminGetAsync("/admin/reconciliation"));

        Assert.Equal("BET", ledger[0].GetProperty("type").GetString());
        Assert.Equal(-250, ledger[0].GetProperty("amount").GetInt64());
        Assert.True(reconciliation.GetProperty("allMatch").GetBoolean());
    }
}
