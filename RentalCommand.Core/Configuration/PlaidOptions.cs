namespace RentalCommand.Core.Configuration;

public class PlaidOptions
{
    public const string SectionName = "Plaid";

    public string Environment { get; set; } = "sandbox";
    public string ClientId { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public string? RedirectUri { get; set; }
    public string? AndroidPackageName { get; set; }

    public bool Configured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(Secret);
}
