using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Dotwire.Host;

public sealed class DotwireTokenSigner : IDisposable
{
    private readonly DotwireHostOptions _options;
    private readonly RSA _rsa;
    private readonly SigningCredentials _creds;
    private readonly JsonWebTokenHandler _jwt = new();

    public DotwireTokenSigner(DotwireHostOptions options)
    {
        this._options = options;
        this._rsa = RSA.Create();
        this._rsa.ImportFromPem(options.PrivateKeyPem);

        var signingKey = new RsaSecurityKey(_rsa)
        {
            KeyId = options.KeyId
        };
        _creds = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);
    }

    public string MintToken(
        string userId,
        string role = "member",
        string? displayName = null,
        TimeSpan? ttl = null
    )
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            NotBefore = now,
            Expires = now.Add(ttl ?? TimeSpan.FromMinutes(15)),
            SigningCredentials = _creds,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = userId,
                ["dw:role"] = role
            }
        };

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            descriptor.Claims["dw:name"] = displayName;
        }

        return _jwt.CreateToken(descriptor);
    }

    public void Dispose()
    {
        _rsa.Dispose();
    }
}