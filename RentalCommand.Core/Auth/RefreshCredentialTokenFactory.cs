using System.Security.Cryptography;
using System.Text;

namespace RentalCommand.Core.Auth;

/// <summary>
/// Pure deterministic credential factory for the future session cutover. Reconstructing a bearer
/// from its credential id lets a same-operation receipt replay recover from a lost HTTP response
/// without storing a bearer or reversible secret in the database.
/// </summary>
public sealed class RefreshCredentialTokenFactory
{
    public const string Prefix = "rc-refresh-v1";
    public const int MinimumSigningKeyBytes = 32;
    private readonly byte[] _signingKey;

    public RefreshCredentialTokenFactory(string base64SigningKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64SigningKey);
        try
        {
            _signingKey = Convert.FromBase64String(base64SigningKey);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "The refresh credential signing key must be valid base64.",
                nameof(base64SigningKey),
                exception);
        }

        if (_signingKey.Length < MinimumSigningKeyBytes)
        {
            throw new ArgumentException(
                $"The refresh credential signing key must contain at least {MinimumSigningKeyBytes} bytes.",
                nameof(base64SigningKey));
        }
    }

    public string CreateBearer(Guid credentialId)
    {
        if (credentialId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(credentialId));
        }

        var id = credentialId.ToString("N");
        var signature = Sign($"{Prefix}:{id}");
        return $"{Prefix}.{id}.{Base64Url(signature)}";
    }

    public string HashBearer(string bearer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearer);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bearer)));
    }

    public bool TryValidateAndReadCredentialId(string bearer, out Guid credentialId)
    {
        credentialId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(bearer))
        {
            return false;
        }

        var parts = bearer.Split('.', StringSplitOptions.None);
        if (parts.Length != 3 ||
            !string.Equals(parts[0], Prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(parts[1], "N", out var parsed))
        {
            return false;
        }

        byte[] provided;
        try
        {
            provided = FromBase64Url(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = Sign($"{Prefix}:{parts[1]}");
        if (provided.Length != expected.Length ||
            !CryptographicOperations.FixedTimeEquals(provided, expected))
        {
            return false;
        }

        credentialId = parsed;
        return true;
    }

    private byte[] Sign(string value)
    {
        using var hmac = new HMACSHA256(_signingKey);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid base64url value."),
        };
        return Convert.FromBase64String(normalized);
    }
}
