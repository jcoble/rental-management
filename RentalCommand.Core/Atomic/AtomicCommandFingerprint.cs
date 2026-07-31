using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RentalCommand.Core.Atomic;

/// <summary>Marks a root command property that is intentionally excluded from receipt fingerprints.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AtomicFingerprintIgnoreAttribute : Attribute;

/// <summary>Produces the stable command fingerprint stored with an atomic receipt.</summary>
public static class AtomicCommandFingerprint
{
    public static string Create(IAtomicCommandData command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var ignoredProperties = IgnoredRootPropertyNames(command.GetType());
        var element = JsonSerializer.SerializeToElement(command, command.GetType());
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(writer, element, ignoredProperties, isRoot: true);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))))
            .ToLowerInvariant();
    }

    private static HashSet<string> IgnoredRootPropertyNames(Type commandType) =>
        commandType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property =>
                property.GetCustomAttribute<AtomicFingerprintIgnoreAttribute>() is not null
                || commandType.GetInterfaces()
                    .SelectMany(contract => contract.GetProperties())
                    .Any(contractProperty =>
                        contractProperty.Name == property.Name
                        && contractProperty.PropertyType == property.PropertyType
                        && contractProperty.GetCustomAttribute<AtomicFingerprintIgnoreAttribute>() is not null))
            .Select(property =>
                property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name)
            .ToHashSet(StringComparer.Ordinal);

    private static void WriteCanonical(
        Utf8JsonWriter writer,
        JsonElement element,
        IReadOnlySet<string> ignoredRootProperties,
        bool isRoot = false)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .Where(property =>
                                 !isRoot || !ignoredRootProperties.Contains(property.Name))
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value, ignoredRootProperties);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item, ignoredRootProperties);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
