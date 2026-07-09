using System.Text.Json;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Configuration;

public static class PortfolioProrationSettings
{
    public const string SettingsKey = "prorationConvention";

    public static ProrationConvention ReadConvention(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return ProrationConvention.ActualDays;
        }

        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (!doc.RootElement.TryGetProperty(SettingsKey, out var element))
            {
                return ProrationConvention.ActualDays;
            }

            if (element.ValueKind == JsonValueKind.String
                && Enum.TryParse<ProrationConvention>(element.GetString(), ignoreCase: true, out var value))
            {
                return value;
            }

            if (element.ValueKind == JsonValueKind.Number
                && element.TryGetInt32(out var numeric)
                && Enum.IsDefined(typeof(ProrationConvention), numeric))
            {
                return (ProrationConvention)numeric;
            }
        }
        catch (JsonException)
        {
            return ProrationConvention.ActualDays;
        }

        return ProrationConvention.ActualDays;
    }
}
