using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Compare.Contracts;

public static class ApiKeyAuth
{
    public const string Header = "X-Api-Key";

    /// <summary>Rejects requests without the right key. An empty configured key rejects everything (fail closed).</summary>
    public static IApplicationBuilder UseApiKey(this IApplicationBuilder app, string expectedKey, params string[] openPaths)
    {
        var expected = Digest(expectedKey);
        return app.Use(async (ctx, next) =>
        {
            if (openPaths.Any(p => ctx.Request.Path.Equals(p, StringComparison.OrdinalIgnoreCase)))
            {
                await next();
                return;
            }

            var supplied = ctx.Request.Headers[Header].ToString();
            if (expectedKey.Length > 0 && CryptographicOperations.FixedTimeEquals(Digest(supplied), expected))
            {
                await next();
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        });
    }

    static byte[] Digest(string s) => SHA256.HashData(Encoding.UTF8.GetBytes(s));
}
