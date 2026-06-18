using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Models.Accounting;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// QuickBooks Online implementation of <see cref="IAccountingProvider"/> — provider #1.
/// Ported from EdiPlatform's <c>QuickBooksErpProvider</c> and re-skinned to the
/// pull-first, provider-neutral surface (§2.1): every authenticated call receives an
/// already-decrypted <see cref="AcctCallCtx"/> and the provider-neutral
/// <see cref="AccountingAppSettings"/>, so this class never reaches into the persisted
/// connection or a credential store (EdiPlatform read <c>SystemSettings</c>; Rental
/// Command resolves creds from <c>QuickBooksOptions</c> upstream).
///
/// <para>
/// This is the ONLY type in the accounting backbone that names QuickBooks / speaks the
/// Intuit wire format. The connection service, import engine, workers, ledger, and
/// settings shell go through <see cref="IAccountingProvider"/> only (AC-1).
/// </para>
///
/// <para>
/// OAuth notes (Intuit, current as of 2023+): the access token lives ~1h; the refresh
/// token rolls on every refresh with a 5-year hard cap; both rotate, so the caller writes
/// both back atomically. No PKCE.
/// </para>
/// </summary>
public sealed class QuickBooksAccountingProvider : IAccountingProvider
{
    private const string AuthorizeUrl = "https://appcenter.intuit.com/connect/oauth2";
    private const string TokenUrl = "https://oauth.platform.intuit.com/oauth2/v1/tokens/bearer";
    private const string RevokeUrl = "https://developer.api.intuit.com/v2/oauth2/tokens/revoke";
    private const string Scope = "com.intuit.quickbooks.accounting";

    private const string ApiBase = "https://quickbooks.api.intuit.com";
    private const string SandboxApiBase = "https://sandbox-quickbooks.api.intuit.com";
    private const int MinorVersion = 70;
    private const int MaxResults = 1000;
    private const int QbDocNumberMaxLength = 21;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<QuickBooksAccountingProvider> _logger;

    public QuickBooksAccountingProvider(
        HttpClient httpClient,
        ILogger<QuickBooksAccountingProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public AccountingProvider Provider => AccountingProvider.QuickBooks;

    /// <summary>
    /// QuickBooks can pull every resource the import engine needs and push income +
    /// expense documents. The backbone branches on these flags, never on the provider
    /// name (AC-1). Push is implemented here but only wired by v1.1 (D-2).
    /// </summary>
    public AccountingCapabilities Capabilities { get; } = new(
        CanPullCustomers: true,
        CanPullVendors: true,
        CanPullAccounts: true,
        CanPullPayments: true,
        CanPullExpenses: true,
        CanPushIncome: true,
        CanPushExpense: true);

    // --- OAuth ---------------------------------------------------------------------

    public string BuildAuthorizeUrl(
        AccountingAppSettings settings, string redirectUri, string state, string? codeChallenge)
    {
        if (string.IsNullOrWhiteSpace(settings.ClientId))
        {
            throw new InvalidOperationException("QuickBooks ClientId not configured.");
        }

        // QuickBooks does not use PKCE; codeChallenge is ignored (it is null for QBO).
        return AuthorizeUrl
            + $"?client_id={Uri.EscapeDataString(settings.ClientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + "&response_type=code"
            + $"&scope={Uri.EscapeDataString(Scope)}"
            + $"&state={Uri.EscapeDataString(state)}";
    }

    public async Task<AccountingTokenResult> ExchangeCodeAsync(
        AccountingAppSettings settings, AccountingCallback callback, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(callback.Realm))
        {
            throw new InvalidOperationException(
                "QuickBooks callback missing realmId — cannot identify the company.");
        }

        var token = await PostTokenRequestAsync(settings, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = callback.Code,
            ["redirect_uri"] = settings.RedirectUri
                ?? throw new InvalidOperationException(
                    "QuickBooks token exchange requires the redirect URI used at authorize."),
        }, "token exchange", cancellationToken);

        return new AccountingTokenResult(
            AccessToken: token.AccessToken
                ?? throw new InvalidOperationException("QuickBooks returned no access_token."),
            RefreshToken: token.RefreshToken
                ?? throw new InvalidOperationException("QuickBooks returned no refresh_token."),
            ExpiresAtUtc: DateTime.UtcNow.AddSeconds(token.ExpiresIn),
            ExternalAccountId: callback.Realm,
            CompanyName: null);
    }

    public async Task<AccountingTokenResult> RefreshTokenAsync(
        AccountingAppSettings settings, string refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("Cannot refresh QuickBooks token: no refresh token.");
        }

        var token = await PostTokenRequestAsync(settings, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, "token refresh", cancellationToken);

        return new AccountingTokenResult(
            AccessToken: token.AccessToken
                ?? throw new InvalidOperationException("QuickBooks returned no access_token."),
            RefreshToken: token.RefreshToken
                ?? throw new InvalidOperationException("QuickBooks returned no refresh_token."),
            ExpiresAtUtc: DateTime.UtcNow.AddSeconds(token.ExpiresIn),
            ExternalAccountId: null,
            CompanyName: null);
    }

    public async Task RevokeAsync(
        AccountingAppSettings settings, string refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, RevokeUrl);
            request.Headers.Authorization = BasicAuth(settings);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { token = refreshToken }),
                Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "QuickBooks revoke returned {Status}: {Body} — proceeding with local disconnect",
                    response.StatusCode, body);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "QuickBooks revoke failed — proceeding with local disconnect");
        }
    }

    private async Task<QbTokenResponse> PostTokenRequestAsync(
        AccountingAppSettings settings,
        Dictionary<string, string> form,
        string operation,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
        request.Headers.Authorization = BasicAuth(settings);
        request.Content = new FormUrlEncodedContent(form);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Never log the request body — it carries the code/refresh token.
            _logger.LogError("QuickBooks {Operation} failed: {Status}", operation, response.StatusCode);
            throw new HttpRequestException(
                $"QuickBooks {operation} failed: {(int)response.StatusCode} {response.StatusCode}",
                inner: null, statusCode: response.StatusCode);
        }

        return JsonSerializer.Deserialize<QbTokenResponse>(json, JsonOptions)
            ?? throw new InvalidOperationException($"QuickBooks returned empty {operation} response.");
    }

    private static AuthenticationHeaderValue BasicAuth(AccountingAppSettings settings)
    {
        var clientId = settings.ClientId
            ?? throw new InvalidOperationException("QuickBooks ClientId not configured.");
        var clientSecret = settings.ClientSecret
            ?? throw new InvalidOperationException("QuickBooks ClientSecret not configured.");
        return new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));
    }

    // --- PULL ----------------------------------------------------------------------

    public Task<AccountingPullResult<ExtCustomerDto>> PullCustomersAsync(
        AcctCallCtx ctx, DateTime? since, CancellationToken cancellationToken)
        => PullEntityAsync(ctx, "Customer", since, ParseCustomer, cancellationToken);

    public Task<AccountingPullResult<ExtVendorDto>> PullVendorsAsync(
        AcctCallCtx ctx, DateTime? since, CancellationToken cancellationToken)
        => PullEntityAsync(ctx, "Vendor", since, ParseVendor, cancellationToken);

    /// <summary>
    /// Accounts and classes are two distinct QBO list types; the import engine treats both
    /// as <see cref="ExtAccountDto"/> (distinguished by <see cref="AccountingListKind"/>).
    /// We pull both and concatenate; the high-water mark is the max across the two.
    /// </summary>
    public async Task<AccountingPullResult<ExtAccountDto>> PullAccountsAsync(
        AcctCallCtx ctx, DateTime? since, CancellationToken cancellationToken)
    {
        var accounts = await PullEntityAsync(
            ctx, "Account", since,
            el => ParseAccount(el, AccountingListKind.Account), cancellationToken);
        var classes = await PullEntityAsync(
            ctx, "Class", since,
            el => ParseAccount(el, AccountingListKind.Class), cancellationToken);

        var items = accounts.Items.Concat(classes.Items).ToList();
        var maxUpdated = MaxNullable(accounts.MaxUpdatedAtUtc, classes.MaxUpdatedAtUtc);
        return new AccountingPullResult<ExtAccountDto>(items, maxUpdated, MoreAvailable: false);
    }

    public Task<AccountingPullResult<ExtPaymentDto>> PullPaymentsAsync(
        AcctCallCtx ctx, DateTime? since, CancellationToken cancellationToken)
        => PullEntityAsync(ctx, "Payment", since, ParsePayment, cancellationToken);

    /// <summary>
    /// Money-out is two QBO transaction types: <c>Purchase</c> (cheque/cash/card spend) and
    /// <c>Bill</c> (A/P). The import engine maps both to RC <c>Expense</c>; we pull both and
    /// concatenate, tagging the external type via the DTO so the ledger keys them apart.
    /// </summary>
    public async Task<AccountingPullResult<ExtExpenseDto>> PullExpensesAsync(
        AcctCallCtx ctx, DateTime? since, CancellationToken cancellationToken)
    {
        var purchases = await PullEntityAsync(
            ctx, "Purchase", since, el => ParseExpense(el, "Purchase"), cancellationToken);
        var bills = await PullEntityAsync(
            ctx, "Bill", since, el => ParseExpense(el, "Bill"), cancellationToken);

        var items = purchases.Items.Concat(bills.Items).ToList();
        var maxUpdated = MaxNullable(purchases.MaxUpdatedAtUtc, bills.MaxUpdatedAtUtc);
        return new AccountingPullResult<ExtExpenseDto>(items, maxUpdated, MoreAvailable: false);
    }

    /// <summary>
    /// Generic QBO list pull: <c>SELECT * FROM {entity}</c> with a <c>MetaData.LastUpdatedTime</c>
    /// delta filter + STARTPOSITION/MAXRESULTS paging until a short page, projecting each element
    /// via <paramref name="parse"/> and tracking the high-water mark. Ported from EdiPlatform's
    /// per-resource <c>PullCustomersAsync</c>/<c>PullPaymentsAsync</c> loop, generalized.
    /// </summary>
    private async Task<AccountingPullResult<T>> PullEntityAsync<T>(
        AcctCallCtx ctx,
        string entity,
        DateTime? since,
        Func<JsonElement, (T Dto, DateTime? Updated)> parse,
        CancellationToken cancellationToken)
    {
        var dtos = new List<T>();
        DateTime? maxUpdatedAt = since;
        int startPosition = 1;
        bool more;
        do
        {
            // Back up the cursor a minute to tolerate clock skew between the cursor and QBO's clock.
            var whereClause = since.HasValue
                ? $" WHERE MetaData.LastUpdatedTime > '{since.Value.AddMinutes(-1):yyyy-MM-ddTHH:mm:ssZ}'"
                : string.Empty;
            var query = $"SELECT * FROM {entity}{whereClause} STARTPOSITION {startPosition} MAXRESULTS {MaxResults}";

            using var doc = await QueryAsync(ctx, query, cancellationToken);
            if (!doc.RootElement.TryGetProperty("QueryResponse", out var qr)
                || !qr.TryGetProperty(entity, out var rows)
                || rows.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            int count = 0;
            foreach (var row in rows.EnumerateArray())
            {
                var (dto, updated) = parse(row);
                dtos.Add(dto);
                if (updated.HasValue && (maxUpdatedAt == null || updated > maxUpdatedAt))
                {
                    maxUpdatedAt = updated;
                }
                count++;
            }

            more = count >= MaxResults;
            startPosition += count;
        } while (more);

        return new AccountingPullResult<T>(dtos, maxUpdatedAt, MoreAvailable: false);
    }

    // --- PULL element parsers (QBO JSON → neutral DTOs) ----------------------------

    private static (ExtCustomerDto, DateTime?) ParseCustomer(JsonElement c)
    {
        var updated = LastUpdated(c);
        return (new ExtCustomerDto(
            ExternalId: GetString(c, "Id"),
            DisplayName: GetString(c, "DisplayName"),
            IsActive: GetBool(c, "Active"),
            UpdatedAtUtc: updated,
            Email: GetNested(c, "PrimaryEmailAddr", "Address"),
            Phone: GetNested(c, "PrimaryPhone", "FreeFormNumber"),
            MetadataJson: c.GetRawText()), updated);
    }

    private static (ExtVendorDto, DateTime?) ParseVendor(JsonElement v)
    {
        var updated = LastUpdated(v);
        return (new ExtVendorDto(
            ExternalId: GetString(v, "Id"),
            DisplayName: GetString(v, "DisplayName"),
            IsActive: GetBool(v, "Active"),
            UpdatedAtUtc: updated,
            Email: GetNested(v, "PrimaryEmailAddr", "Address"),
            Phone: GetNested(v, "PrimaryPhone", "FreeFormNumber"),
            TaxId: GetStringOrNull(v, "TaxIdentifier"),
            MetadataJson: v.GetRawText()), updated);
    }

    private static (ExtAccountDto, DateTime?) ParseAccount(JsonElement a, AccountingListKind kind)
    {
        var updated = LastUpdated(a);
        return (new ExtAccountDto(
            ExternalId: GetString(a, "Id"),
            Name: GetString(a, "Name"),
            Kind: kind,
            IsActive: GetBool(a, "Active"),
            UpdatedAtUtc: updated,
            // Classes carry no AccountType; only accounts do.
            AccountType: kind == AccountingListKind.Account ? GetStringOrNull(a, "AccountType") : null,
            MetadataJson: a.GetRawText()), updated);
    }

    private static (ExtPaymentDto, DateTime?) ParsePayment(JsonElement p)
    {
        var updated = LastUpdated(p);

        List<string>? invoiceIds = null;
        if (p.TryGetProperty("Line", out var lines) && lines.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in lines.EnumerateArray())
            {
                if (!line.TryGetProperty("LinkedTxn", out var linked)
                    || linked.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var lt in linked.EnumerateArray())
                {
                    if (lt.TryGetProperty("TxnType", out var tt)
                        && tt.GetString() == "Invoice"
                        && lt.TryGetProperty("TxnId", out var tid))
                    {
                        (invoiceIds ??= new List<string>()).Add(tid.GetString() ?? "");
                    }
                }
            }
        }

        return (new ExtPaymentDto(
            ExternalId: GetString(p, "Id"),
            CustomerExternalId: GetRefValue(p, "CustomerRef"),
            Amount: GetDecimal(p, "TotalAmt"),
            TxnDateUtc: GetDate(p, "TxnDate"),
            PaymentMethod: GetRefValue(p, "PaymentMethodRef"),
            ReferenceNumber: GetStringOrNull(p, "PaymentRefNum"),
            UpdatedAtUtc: updated,
            InvoiceExternalIds: invoiceIds,
            MetadataJson: p.GetRawText()), updated);
    }

    /// <summary>
    /// Projects a QBO Purchase or Bill into the neutral expense DTO. Vendor comes from
    /// <c>EntityRef</c> (Purchase) or <c>VendorRef</c> (Bill); the account/class are read from
    /// the first detail line that carries them.
    /// </summary>
    private static (ExtExpenseDto, DateTime?) ParseExpense(JsonElement e, string sourceType)
    {
        var updated = LastUpdated(e);

        string? vendorRef = sourceType == "Bill"
            ? GetRefValue(e, "VendorRef")
            : GetRefValue(e, "EntityRef");

        string? accountRef = null;
        string? classRef = null;
        if (e.TryGetProperty("Line", out var lines) && lines.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in lines.EnumerateArray())
            {
                // AccountBasedExpenseLineDetail carries AccountRef (+ optional ClassRef).
                if (line.TryGetProperty("AccountBasedExpenseLineDetail", out var detail))
                {
                    accountRef ??= GetRefValue(detail, "AccountRef");
                    classRef ??= GetRefValue(detail, "ClassRef");
                }

                if (accountRef != null && classRef != null)
                {
                    break;
                }
            }
        }

        // Purchase exposes the account directly via AccountRef when there is no line-level account.
        accountRef ??= GetRefValue(e, "AccountRef");

        return (new ExtExpenseDto(
            ExternalId: GetString(e, "Id"),
            VendorExternalId: vendorRef,
            AccountExternalId: accountRef,
            ClassExternalId: classRef,
            Amount: GetDecimal(e, "TotalAmt"),
            TxnDateUtc: GetDate(e, "TxnDate"),
            ReferenceNumber: GetStringOrNull(e, "DocNumber"),
            UpdatedAtUtc: updated,
            MetadataJson: e.GetRawText()), updated);
    }

    // --- PUSH ----------------------------------------------------------------------
    //
    // Ported from EdiPlatform's PushInvoiceAsync + CheckForDuplicateAsync. Real ports, not
    // stubs — the provider owns them — but only wired by v1.1 (D-2); the import (pull) path is v1.

    public async Task<AcctPushResult> UpsertIncomeAsync(
        AcctCallCtx ctx, AcctIncomeDoc doc, CancellationToken cancellationToken)
    {
        var docNumber = ToQuickBooksDocNumber(doc.ExternalDocNumber);

        // Idempotency: query by DocNumber first (survives a lost ledger row, e.g. DB restore).
        var existingId = await FindByDocNumberAsync(ctx, "SalesReceipt", docNumber, cancellationToken);
        if (existingId != null)
        {
            return new AcctPushResult(AcctPushOutcome.AlreadyExisted, existingId);
        }

        var payload = new
        {
            DocNumber = docNumber,
            TxnDate = doc.TxnDateUtc.ToString("yyyy-MM-dd"),
            PrivateNote = doc.Memo,
            CustomerRef = doc.CustomerExternalId == null ? null : new { value = doc.CustomerExternalId },
            Line = new object[]
            {
                new
                {
                    Amount = doc.Amount,
                    DetailType = "SalesItemLineDetail",
                    SalesItemLineDetail = new { },
                },
            },
        };

        var id = await PostObjectAsync(ctx, "salesreceipt", "SalesReceipt", payload, cancellationToken);
        return new AcctPushResult(AcctPushOutcome.Created, id);
    }

    public async Task<AcctPushResult> UpsertExpenseAsync(
        AcctCallCtx ctx, AcctExpenseDoc doc, CancellationToken cancellationToken)
    {
        var docNumber = ToQuickBooksDocNumber(doc.ExternalDocNumber);

        var existingId = await FindByDocNumberAsync(ctx, "Purchase", docNumber, cancellationToken);
        if (existingId != null)
        {
            return new AcctPushResult(AcctPushOutcome.AlreadyExisted, existingId);
        }

        var payload = new
        {
            DocNumber = docNumber,
            TxnDate = doc.TxnDateUtc.ToString("yyyy-MM-dd"),
            PrivateNote = doc.Memo,
            PaymentType = "Cash",
            AccountRef = doc.AccountExternalId == null ? null : new { value = doc.AccountExternalId },
            EntityRef = doc.VendorExternalId == null ? null : new { value = doc.VendorExternalId },
            Line = new object[]
            {
                new
                {
                    Amount = doc.Amount,
                    DetailType = "AccountBasedExpenseLineDetail",
                    AccountBasedExpenseLineDetail = doc.AccountExternalId == null
                        ? (object)new { }
                        : new { AccountRef = new { value = doc.AccountExternalId } },
                },
            },
        };

        var id = await PostObjectAsync(ctx, "purchase", "Purchase", payload, cancellationToken);
        return new AcctPushResult(AcctPushOutcome.Created, id);
    }

    private async Task<string?> FindByDocNumberAsync(
        AcctCallCtx ctx, string entity, string docNumber, CancellationToken cancellationToken)
    {
        try
        {
            var query = $"SELECT Id FROM {entity} WHERE DocNumber = '{docNumber.Replace("'", "''")}'";
            using var doc = await QueryAsync(ctx, query, cancellationToken);
            if (doc.RootElement.TryGetProperty("QueryResponse", out var qr)
                && qr.TryGetProperty(entity, out var rows)
                && rows.ValueKind == JsonValueKind.Array
                && rows.GetArrayLength() > 0)
            {
                return rows[0].GetProperty("Id").GetString();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "QuickBooks idempotency check failed for {Entity} {DocNumber} — proceeding with create",
                entity, docNumber);
        }

        return null;
    }

    private async Task<string> PostObjectAsync(
        AcctCallCtx ctx, string path, string responseKey, object payload, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl(ctx)}/v3/company/{ctx.Realm}/{path}?minorversion={MinorVersion}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = BuildQuickBooksHttpError($"{path} push", response, body);
            _logger.LogError("QuickBooks {Path} push failed: {Status} {Body}", path, response.StatusCode, body);
            throw new HttpRequestException(message, inner: null, statusCode: response.StatusCode);
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty(responseKey).GetProperty("Id").GetString()
            ?? throw new InvalidOperationException($"QuickBooks returned no {responseKey} Id.");
    }

    // --- Shared QBO HTTP + parsing helpers (ported from QuickBooksErpProvider) ------

    private static string BaseUrl(AcctCallCtx ctx) => ctx.UseSandbox ? SandboxApiBase : ApiBase;

    private async Task<JsonDocument> QueryAsync(AcctCallCtx ctx, string query, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl(ctx)}/v3/company/{ctx.Realm}/query"
            + $"?query={Uri.EscapeDataString(query)}&minorversion={MinorVersion}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("QuickBooks query failed: {Status} {Body}", response.StatusCode, body);
            throw new HttpRequestException(
                BuildQuickBooksHttpError("query", response, body),
                inner: null, statusCode: response.StatusCode);
        }

        return JsonDocument.Parse(body);
    }

    /// <summary>
    /// Caps a Rental Command external doc number at QuickBooks' 21-char DocNumber limit,
    /// hashing the overflow into a stable suffix so the same input always yields the same
    /// (idempotency-safe) doc number. Ported verbatim from <c>QuickBooksErpProvider</c>.
    /// </summary>
    internal static string ToQuickBooksDocNumber(string docNumber)
    {
        var trimmed = docNumber.Trim();
        if (trimmed.Length <= QbDocNumberMaxLength)
        {
            return trimmed;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed)))[..8];
        var prefixLength = QbDocNumberMaxLength - hash.Length - 1;
        var prefix = trimmed[..prefixLength].TrimEnd('-', '_', ' ');

        return string.IsNullOrWhiteSpace(prefix) ? hash : $"{prefix}-{hash}";
    }

    private static string BuildQuickBooksHttpError(string operation, HttpResponseMessage response, string body)
    {
        var baseMessage = $"QuickBooks {operation} failed: {(int)response.StatusCode} {response.StatusCode}";
        var detail = ExtractQuickBooksFault(body);
        return string.IsNullOrWhiteSpace(detail) ? baseMessage : $"{baseMessage} - {detail}";
    }

    private static string? ExtractQuickBooksFault(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("Fault", out var fault)
                || !fault.TryGetProperty("Error", out var errors)
                || errors.ValueKind != JsonValueKind.Array
                || errors.GetArrayLength() == 0)
            {
                return null;
            }

            var error = errors[0];
            var message = error.TryGetProperty("Message", out var m) ? m.GetString() : null;
            var detail = error.TryGetProperty("Detail", out var d) ? d.GetString() : null;
            var code = error.TryGetProperty("code", out var c) ? c.GetString() : null;

            var parts = new[] { message, detail, string.IsNullOrWhiteSpace(code) ? null : $"code {code}" }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part!.Trim())
                .ToArray();

            return parts.Length == 0 ? null : string.Join(" / ", parts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // --- Small JSON accessors ------------------------------------------------------

    private static string GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) ? v.GetString() ?? string.Empty : string.Empty;

    private static string? GetStringOrNull(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();

    private static decimal GetDecimal(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

    private static DateTime GetDate(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && DateTime.TryParse(v.GetString(), out var parsed)
            ? DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc)
            : DateTime.UtcNow.Date;

    private static string? GetRefValue(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var r) && r.TryGetProperty("value", out var v) ? v.GetString() : null;

    private static string? GetNested(JsonElement el, string outerProp, string innerProp)
        => el.TryGetProperty(outerProp, out var outer)
            && outer.TryGetProperty(innerProp, out var inner)
            ? inner.GetString()
            : null;

    private static DateTime? LastUpdated(JsonElement el)
        => el.TryGetProperty("MetaData", out var md)
            && md.TryGetProperty("LastUpdatedTime", out var lu)
            && DateTime.TryParse(lu.GetString(), out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    private static DateTime? MaxNullable(DateTime? a, DateTime? b)
        => a == null ? b : b == null ? a : (a > b ? a : b);

    private sealed class QbTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("x_refresh_token_expires_in")] public int RefreshTokenExpiresIn { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    }
}
