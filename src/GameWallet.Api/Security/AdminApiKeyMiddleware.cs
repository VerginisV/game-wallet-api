using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace GameWallet.Api.Security;

public sealed class AdminOptions
{
    public List<string> ApiKeys { get; set; } = [];
}

/// <summary>The operator's back office uses an X-Api-Key header on /admin.</summary>
public sealed class AdminApiKeyMiddleware(RequestDelegate next, IOptions<AdminOptions> options)
{
    public const string HeaderName = "X-Api-Key";

    private readonly byte[][] _keys = [.. options.Value.ApiKeys.Where(k => !string.IsNullOrEmpty(k)).Select(Encoding.UTF8.GetBytes)];

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/admin") && !IsAuthorized(context.Request.Headers[HeaderName].ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { title = "Missing or invalid X-Api-Key header", status = 401 });
            return;
        }

        await next(context);
    }

    private bool IsAuthorized(string provided)
    {
        if (string.IsNullOrEmpty(provided))
            return false;
        var bytes = Encoding.UTF8.GetBytes(provided);
        return _keys.Any(k => CryptographicOperations.FixedTimeEquals(k, bytes));
    }
}
