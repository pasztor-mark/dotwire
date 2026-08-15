using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace dotwire.Tests;

/// <summary>
/// The tests play the HOST: they hold a throwaway private key and mint tokens
/// (AUTH.md's host-side example). dotwire itself only ever sees the public PEM.
/// </summary>
public static class TestKeys
{
    public const string Kid = "test-key-1";
    public static readonly RSA Rsa = RSA.Create(2048);
    public static string PublicPem { get; } = Rsa.ExportSubjectPublicKeyInfoPem();
}

public static class TestTokens
{
    public const string Issuer = "test-host";
    public const string Audience = "dotwire";

    public static string Mint(
        string sub,
        string? role = "member",
        string? issuer = Issuer,
        string? audience = Audience,
        TimeSpan? ttl = null,
        RSA? rsa = null,
        string kid = TestKeys.Kid)
    {
        var claims = new Dictionary<string, object> { ["sub"] = sub };
        if (role is not null)
            claims["dw:role"] = role;

        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            NotBefore = now.AddMinutes(-1),
            IssuedAt = now,
            Expires = now.Add(ttl ?? TimeSpan.FromMinutes(15)),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(rsa ?? TestKeys.Rsa) { KeyId = kid },
                SecurityAlgorithms.RsaSha256),
        });
    }
}
