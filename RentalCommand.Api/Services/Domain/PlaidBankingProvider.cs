using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace RentalCommand.Api.Services.Domain;

public interface IPlaidBankingProvider
{
    Task<PlaidLinkTokenResult> CreateLinkTokenAsync(PlaidRuntimeSettings settings, int portfolioId, int userId, CancellationToken ct = default);
    Task<PlaidExchangeResult> ExchangePublicTokenAsync(PlaidRuntimeSettings settings, string publicToken, CancellationToken ct = default);
    Task<PlaidTransactionsSyncResult> SyncTransactionsAsync(PlaidRuntimeSettings settings, string accessToken, string? cursor, CancellationToken ct = default);
}

public sealed record PlaidRuntimeSettings(
    string Environment,
    string ClientId,
    string Secret,
    string? RedirectUri,
    string? AndroidPackageName)
{
    public bool Configured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(Secret);
}

public sealed record PlaidLinkTokenResult(string LinkToken, DateTime? Expiration, string? RequestId);

public sealed record PlaidExchangeResult(string AccessToken, string ItemId, string? RequestId);

public sealed record PlaidTransactionsSyncResult(
    string? NextCursor,
    IReadOnlyList<PlaidSyncedTransaction> Added,
    IReadOnlyList<PlaidSyncedTransaction> Modified,
    IReadOnlyList<string> RemovedTransactionIds,
    string? RequestId);

public sealed record PlaidSyncedTransaction(
    string TransactionId,
    string AccountId,
    DateTime PostedAt,
    DateTime? AuthorizedAt,
    string Description,
    string? MerchantName,
    decimal Amount,
    string IsoCurrencyCode,
    string? Category,
    string RawData);

public sealed class PlaidBankingProvider : IPlaidBankingProvider
{
    private readonly HttpClient _http;

    public PlaidBankingProvider(HttpClient http)
    {
        _http = http;
    }

    public async Task<PlaidLinkTokenResult> CreateLinkTokenAsync(
        PlaidRuntimeSettings settings,
        int portfolioId,
        int userId,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["client_name"] = "Rental Command",
            ["country_codes"] = new[] { "US" },
            ["language"] = "en",
            ["user"] = new { client_user_id = $"portfolio-{portfolioId}-user-{userId}" },
            ["products"] = new[] { "transactions" },
        };
        if (!string.IsNullOrWhiteSpace(settings.RedirectUri))
        {
            body["redirect_uri"] = settings.RedirectUri.Trim();
        }
        if (!string.IsNullOrWhiteSpace(settings.AndroidPackageName))
        {
            body["android_package_name"] = settings.AndroidPackageName.Trim();
        }

        var response = await PostAsync<PlaidLinkTokenCreateResponse>(
            settings,
            "/link/token/create",
            body,
            ct);

        return new PlaidLinkTokenResult(response.LinkToken, response.Expiration, response.RequestId);
    }

    public async Task<PlaidExchangeResult> ExchangePublicTokenAsync(
        PlaidRuntimeSettings settings,
        string publicToken,
        CancellationToken ct = default)
    {
        var response = await PostAsync<PlaidPublicTokenExchangeResponse>(
            settings,
            "/item/public_token/exchange",
            new { public_token = publicToken },
            ct);

        return new PlaidExchangeResult(response.AccessToken, response.ItemId, response.RequestId);
    }

    public async Task<PlaidTransactionsSyncResult> SyncTransactionsAsync(
        PlaidRuntimeSettings settings,
        string accessToken,
        string? cursor,
        CancellationToken ct = default)
    {
        var response = await PostAsync<PlaidTransactionsSyncResponse>(
            settings,
            "/transactions/sync",
            new
            {
                access_token = accessToken,
                cursor,
                count = 100,
            },
            ct);

        var added = response.Added.Select(MapTransaction).ToList();
        var modified = response.Modified.Select(MapTransaction).ToList();
        var removed = response.Removed
            .Select(r => r.TransactionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();

        return new PlaidTransactionsSyncResult(response.NextCursor, added, modified, removed, response.RequestId);
    }

    private async Task<T> PostAsync<T>(
        PlaidRuntimeSettings settings,
        string path,
        object body,
        CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(
            BuildUri(settings.Environment, path),
            MergeCredentials(settings, body),
            cancellationToken: ct);
        if (!response.IsSuccessStatusCode)
        {
            var requestId = response.Headers.TryGetValues("Plaid-Request-Id", out var values)
                ? values.FirstOrDefault()
                : null;
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(requestId)
                    ? $"Plaid request failed with {(int)response.StatusCode}."
                    : $"Plaid request failed with {(int)response.StatusCode}. Request id: {requestId}.");
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Plaid returned an empty response.");
    }

    private static Uri BuildUri(string environment, string path)
    {
        var host = NormalizeEnvironment(environment) switch
        {
            "production" => "https://production.plaid.com",
            "development" => "https://development.plaid.com",
            _ => "https://sandbox.plaid.com",
        };
        return new Uri($"{host}{path}");
    }

    private static object MergeCredentials(PlaidRuntimeSettings settings, object body)
    {
        var fields = body is IDictionary<string, object?> dictionary
            ? dictionary.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
            : body
                .GetType()
                .GetProperties()
                .ToDictionary(p => p.Name, p => p.GetValue(body));
        fields["client_id"] = settings.ClientId;
        fields["secret"] = settings.Secret;
        return fields;
    }

    private static string NormalizeEnvironment(string environment) =>
        string.IsNullOrWhiteSpace(environment) ? "sandbox" : environment.Trim().ToLowerInvariant();

    private static PlaidSyncedTransaction MapTransaction(PlaidTransaction t) => new(
        t.TransactionId,
        t.AccountId,
        DateTime.SpecifyKind(t.Date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
        t.AuthorizedDate.HasValue
            ? DateTime.SpecifyKind(t.AuthorizedDate.Value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)
            : null,
        t.Name ?? t.MerchantName ?? "Bank transaction",
        t.MerchantName,
        t.Amount,
        string.IsNullOrWhiteSpace(t.IsoCurrencyCode) ? "USD" : t.IsoCurrencyCode,
        t.Category?.LastOrDefault(),
        t.RawData.GetRawText());

    private sealed class PlaidLinkTokenCreateResponse
    {
        [JsonPropertyName("link_token")]
        public string LinkToken { get; set; } = string.Empty;

        [JsonPropertyName("expiration")]
        public DateTime? Expiration { get; set; }

        [JsonPropertyName("request_id")]
        public string? RequestId { get; set; }
    }

    private sealed class PlaidPublicTokenExchangeResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("item_id")]
        public string ItemId { get; set; } = string.Empty;

        [JsonPropertyName("request_id")]
        public string? RequestId { get; set; }
    }

    private sealed class PlaidTransactionsSyncResponse
    {
        [JsonPropertyName("added")]
        public IReadOnlyList<PlaidTransaction> Added { get; set; } = [];

        [JsonPropertyName("modified")]
        public IReadOnlyList<PlaidTransaction> Modified { get; set; } = [];

        [JsonPropertyName("removed")]
        public IReadOnlyList<PlaidRemovedTransaction> Removed { get; set; } = [];

        [JsonPropertyName("next_cursor")]
        public string? NextCursor { get; set; }

        [JsonPropertyName("request_id")]
        public string? RequestId { get; set; }
    }

    private sealed class PlaidTransaction
    {
        [JsonPropertyName("transaction_id")]
        public string TransactionId { get; set; } = string.Empty;

        [JsonPropertyName("account_id")]
        public string AccountId { get; set; } = string.Empty;

        [JsonPropertyName("date")]
        public DateOnly Date { get; set; }

        [JsonPropertyName("authorized_date")]
        public DateOnly? AuthorizedDate { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("merchant_name")]
        public string? MerchantName { get; set; }

        [JsonPropertyName("amount")]
        public decimal Amount { get; set; }

        [JsonPropertyName("iso_currency_code")]
        public string? IsoCurrencyCode { get; set; }

        [JsonPropertyName("category")]
        public IReadOnlyList<string>? Category { get; set; }

        [JsonExtensionData]
        public Dictionary<string, System.Text.Json.JsonElement> ExtensionData { get; set; } = [];

        [JsonIgnore]
        public System.Text.Json.JsonElement RawData
        {
            get
            {
                var json = System.Text.Json.JsonSerializer.SerializeToElement(this);
                return json;
            }
        }
    }

    private sealed class PlaidRemovedTransaction
    {
        [JsonPropertyName("transaction_id")]
        public string TransactionId { get; set; } = string.Empty;
    }
}
