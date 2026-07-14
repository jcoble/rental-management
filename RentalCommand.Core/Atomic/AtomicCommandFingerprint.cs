using System.Security.Cryptography;
using System.Text.Json;

namespace RentalCommand.Core.Atomic;

/// <summary>
/// Produces the stable business-payload fingerprint stored with an atomic receipt. Authentication
/// envelope fields and command-generated processing timestamps are deliberately omitted: they can
/// legitimately change when the same request is retried after a token refresh or timeout. Dates and
/// timestamps that describe the business operation remain part of the fingerprint.
/// </summary>
public static class AtomicCommandFingerprint
{
    private static readonly HashSet<string> RetryVolatileProperties = new(StringComparer.Ordinal)
    {
        "AuthSessionId",
        "AccessContextId",
        "AccessRevision",
        "ExpectedAccessRevision",
        "ActorAuthSessionId",
        "ActorAccessContextId",
        "ActorAccessRevision",
        "DeliveryIdempotencyKey",
        // Data Protection uses a randomized nonce, so retrying the same Plaid command produces
        // different ciphertext. The stable request and external-account hashes remain fingerprinted.
        "ExternalAccountIdCipherText",
        "PreparedAtUtc",
        "AppliedAtUtc",
        "AdmittedAtUtc",
        "RecordedAtUtc",
        "ImportedAtUtc",
        "ReceivedAtUtc",
        "ReconciledAtUtc",
        "ConfirmedAtUtc",
        "UploadedAtUtc",
        "IssuedAtUtc",
        "PresentedAtUtc",
        "RequestedAtUtc",
        "GeneratedAtUtc",
        "ChangedAtUtc",
        "DeletedAtUtc",
        "DispatchedAtUtc",
        "CreatedAtUtc",
        "CompletedAtUtc",
        "ExpiresAtUtc",
        "SessionExpiresAtUtc",
        "CredentialExpiresAtUtc",
        "AbsoluteFamilyExpiresAtUtc",
        "ReplacementExpiresAtUtc",
        "LinkExpiresAtUtc",
        // Password commands carry these values only into the in-memory handler. The receipt
        // fingerprint binds the server-keyed PasswordIntentHash instead, so a database leak does
        // not expose an unsalted offline password verifier.
        "CurrentPassword",
        "NewPassword",
    };

    public static string Create(IAtomicCommandData command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var element = JsonSerializer.SerializeToElement(command, command.GetType());
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(writer, element, isRoot: true);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))))
            .ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, bool isRoot = false)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .Where(property => !isRoot || !RetryVolatileProperties.Contains(property.Name))
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
