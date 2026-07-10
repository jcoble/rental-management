using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RentalCommand.Core.Outbox;

/// <summary>Builds bounded, deterministic keys from the stable identity of one logical delivery.</summary>
public static class OutboxIdempotency
{
    public static string Create(string scope, params object?[] parts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var identity = string.Join("\u001f", parts.Select(Format));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var boundedScope = scope.Trim().ToLowerInvariant();
        if (boundedScope.Length > 230) boundedScope = boundedScope[..230];
        return $"{boundedScope}:{hash}";
    }

    private static string Format(object? value) => value switch
    {
        null => "<null>",
        DateTime dateTime => dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty,
    };
}
