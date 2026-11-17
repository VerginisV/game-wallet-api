using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace GameWallet.Api.Security;

public sealed class ProviderOptions
{
    /// <summary>Shared secret per provider ID.</summary>
    public Dictionary<string, string> Secrets { get; set; } = [];

    /// <summary>How old a signed request may be before it is refused.</summary>
    public int MaxClockSkewSeconds { get; set; } = 300;
}

/// <summary>
/// Request signing between the game provider and the wallet:
/// X-Signature = hex(HMAC-SHA256(secret, "{X-Timestamp}.{raw body}")).
/// The timestamp limits replays; the transaction ID makes any replay that gets through harmless.
/// </summary>
public static class ProviderSignature
{
    public static string Compute(string secret, string timestamp, string body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return Convert.ToHexStringLower(hash);
    }

    public static bool IsValid(string secret, string timestamp, string body, string signature)
    {
        var expected = Encoding.ASCII.GetBytes(Compute(secret, timestamp, body));
        var provided = Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    public static bool IsFresh(string timestamp, DateTimeOffset now, int maxSkewSeconds) =>
        long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
        && Math.Abs(now.ToUnixTimeSeconds() - seconds) <= maxSkewSeconds;
}

/// <summary>Checks the provider signature on every /wallet call and records which provider sent it.</summary>
public sealed class ProviderAuthMiddleware(RequestDelegate next, IOptions<ProviderOptions> options, TimeProvider time)
{
    public const string ProviderHeader = "X-Provider-Id";
    public const string TimestampHeader = "X-Timestamp";
    public const string SignatureHeader = "X-Signature";
    public const string ProviderItem = "ProviderId";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/wallet"))
        {
            await next(context);
            return;
        }

        var providerId = context.Request.Headers[ProviderHeader].ToString();
        var timestamp = context.Request.Headers[TimestampHeader].ToString();
        var signature = context.Request.Headers[SignatureHeader].ToString();

        if (!options.Value.Secrets.TryGetValue(providerId, out var secret) || string.IsNullOrEmpty(secret))
        {
            await RejectAsync(context, "Unknown provider");
            return;
        }

        if (!ProviderSignature.IsFresh(timestamp, time.GetUtcNow(), options.Value.MaxClockSkewSeconds))
        {
            await RejectAsync(context, "Missing or expired X-Timestamp");
            return;
        }

        context.Request.EnableBuffering();
        string body;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
            body = await reader.ReadToEndAsync(context.RequestAborted);
        context.Request.Body.Position = 0;

        if (string.IsNullOrEmpty(signature) || !ProviderSignature.IsValid(secret, timestamp, body, signature))
        {
            await RejectAsync(context, "Invalid signature");
            return;
        }

        context.Items[ProviderItem] = providerId;
        await next(context);
    }

    private static Task RejectAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsJsonAsync(new { status = "INVALID_SIGNATURE", message });
    }
}
