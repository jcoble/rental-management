# Phase 1 + 2 — Upload Pipeline + Scan-to-Record Flagship — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax. Phase 0 already created the entity *shapes*, interface *contracts*, config classes, DbSets, and the `20260530231947_InitialCreate` migration. **Most tasks IMPLEMENT/WIRE existing stubs — they do not create new entities or migrations.** Read each task's "Phase 0 state" note before writing code.

**Goal:** Ship the flagship upload-and-scan-to-record pipeline by implementing the Phase-0 stubs: filesystem-backed `IFileStorage`, a real `AnthropicLlmProvider` (Claude Messages API with vision + tool-output + self-reported confidence + deterministic no-op fallback), an async `ScanProcessingWorker` on the existing `EngineWorkerBase`, and `ScanService.ConfirmAndCreateAsync` that turns a confirmed Receipt draft into a real `Expense` via the existing `IExpenseService.CreateAsync`. Receipt→Expense is the first and only extraction target this phase; every proposal is human-confirmed before it becomes a record.

**Architecture:** Phase 0 already added `StoredFile`, `ScanDraft`, `OutboxMessage` entities, the `ILlmProvider`/`IScanService`/`IFileStorage`/`IDataUpdateService`/`IAuditTrailService` interfaces, the `AssistantConfig`/`UploadSettings` config classes, the `RentalCommandDbContext` DbSets, the `DataUpdateHub` (`/api/v1/hubs/updates`) + `DataUpdateService`, the web SignalR client + `invalidate.ts` bridge, and the `EngineWorkerBase` + `OutboxDispatchWorker` template. This phase fills in the *behavior*: (1) extend `ILlmProvider.ExtractAsync` to accept `contentType` + an extraction schema so the provider can route born-digital PDFs text-first and vision otherwise; (2) implement `FileStorage` (disk) + a `ScanFileService` that writes a `StoredFile` row; (3) implement `AnthropicLlmProvider`; (4) implement `ScanService` (`CreateDraftAsync` + new `ConfirmAndCreateAsync`/`RejectDraftAsync`) and `ScanController` on the `/api/v1` convention with `GetPortfolioId()`; (5) implement `ScanProcessingWorker` in Engine; (6) build the web `FileDrop`, `/scan` list, and `/scan/[draftId]` review pages; (7) implement `AuditTrailService`; (8) add the MCP `scan_document` tool. Files are written to disk and the `StoredFile` row committed before the draft is queued; extraction runs off the request path in Engine; on completion the worker broadcasts `Entity Updated("ScanDraft", id)` over the existing hub so the review UI refreshes. Token usage + cost are recorded on `ScanDraft`. Deterministic no-op fallback fires when `AssistantConfig.ApiKey` is unset.

**Tech Stack:** .NET 10, PostgreSQL + EF Core (Npgsql), SvelteKit + Svelte 5 + TanStack Query + svelte-sonner, SignalR, Anthropic Claude Messages API via typed `HttpClient` (no SDK dependency), xUnit + Playwright.

**Depends on:** Phase 0 (4-project structure: `RentalCommand.Core`/`.Data`/`.Api`/`.Engine`; Postgres; Identity/JWT with `portfolioId` claim; `RentalCommandDbContext`; SignalR `DataUpdateHub`; `EngineWorkerBase`; `AuthenticatedPortfolioControllerBase`; `AddDomainServices` DI extension; `PortfolioScopeGuards`).

---

## Ground-truth anchors (exact existing paths/signatures — DO NOT recreate these)

**Entities (`RentalCommand.Core/Entities/`)** — already exist, already in the `InitialCreate` migration:
- `ScanDraft.cs`: `int Id, int PortfolioId, string FilePath, string TargetEntityType, string Status, string? ExtractedFields (jsonb), string? ModelId, int? TokensUsed, decimal? CostUsd, DateTime CreatedAt, DateTime? ReviewedAt, string? ReviewedBy, DateTime? ConfirmedAt, Portfolio? Portfolio`. **`Status` is a plain `string` (no enum). `ExtractedFields` is a nullable `string` mapped `jsonb`. There is NO `TargetEntityId` column** — the link to the created Expense is recorded by re-keying the `StoredFile` and in the audit log.
- `StoredFile.cs`: `int Id, int PortfolioId, string FileName, string FilePath, string ContentType, long FileSize, string? EntityType, int? EntityId, DateTime UploadedAt, DateTime? DeletedAt, Portfolio? Portfolio`. Has a global soft-delete query filter.
- `OutboxMessage.cs`: `long Id, int PortfolioId, string MessageType, string Payload (jsonb), int RetryCount, DateTime CreatedAt, DateTime? SentAt, DateTime? FailedAt, string? Error`. (Not changed this phase.)
- `Expense.cs`: `int Id, int PortfolioId, int? PropertyId, int? VendorId, int? WorkOrderId, ScheduleECategory Category, string Description, ExpenseStatus Status, decimal Amount, DateTime IncurredAt, DateTime? DueDate, DateTime? PaidAt, bool BillableToOwner, string? Notes, ...`.

**Enums (`RentalCommand.Core/Enums/`):** `ScheduleECategory` (Advertising=0, AutoTravel, CleaningMaintenance, Commissions, Insurance, LegalProfessional, ManagementFees, MortgageInterest, Repairs, Supplies, Taxes, Utilities, Depreciation, Other=13); `ExpenseStatus` (Pending, Approved, Paid, Rejected, Draft); `AuditLogOperation` (Created=0, Updated, Deleted, Approved, Rejected).

**Interfaces (`RentalCommand.Core/Interfaces/`)** — contracts exist, implementations mostly do NOT:
- `ILlmProvider.cs`: `Task<string> ChatAsync(string prompt, CancellationToken ct = default)`; `Task<ExtractedFields> ExtractAsync(byte[] imageOrPdfBytes, string prompt, CancellationToken ct = default)`. Nested: `ExtractedFields { Dictionary<string,FieldExtraction> Fields; string ModelId; int TokensUsed; }`, `FieldExtraction { string Value; decimal Confidence; Box? SourceBox; }`, `Box { int Page; double X,Y,Width,Height; }`. **No concrete impl exists.** (Task 1 extends this signature.)
- `IScanService.cs`: `Task<ScanDraft> CreateDraftAsync(int portfolioId, byte[] fileBytes, string contentType, string targetEntityType, CancellationToken ct = default)`. **No impl exists.** (Task 6 implements + extends it.)
- `IFileStorage.cs`: `Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct)`; `Task<Stream> DownloadAsync(string path, CancellationToken ct)`; `Task DeleteAsync(string path, CancellationToken ct)`. **No impl exists.**
- `IDataUpdateService.cs`: `Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct)`; `Task BroadcastEntityDeleteAsync(...)`. **`DataUpdateService` impl EXISTS** and broadcasts `"EntityUpdated"`/`"EntityDeleted"` to group `portfolio-{id}`.
- `IAuditTrailService.cs`: `Task LogAsync(int portfolioId, string entityType, int entityId, AuditLogOperation operation, int? userId = null, string? actorLabel = null, string? oldValues = null, string? newValues = null, string? changeReason = null, string? ipAddress = null, CancellationToken ct = default)`. **No impl exists** (Task 9 adds it). `AuditLog` entity exists.

**Config (`RentalCommand.Core/Configuration/`):** `AssistantConfig { const SectionName="Assistant"; string ModelId; string? ApiKey; int ContextLength; }` — exists, **NOT yet bound in `Program.cs`**. `UploadSettings { const SectionName="Upload"; string BasePath; int MaxFileSizeBytes; string[] AllowedMimeTypes; }` — exists, **NOT yet bound**.

**DbContext:** `RentalCommand.Data/RentalCommandDbContext.cs` (inherits `IdentityDbContext<ApplicationUser, IdentityRole<int>, int>`). DbSets `ScanDrafts`, `StoredFiles`, `OutboxMessages`, `Expenses` already exist. `ScanDraft.ExtractedFields` mapped `jsonb`; `Expense.Category` mapped `.HasConversion<int>()`. **The only migration is `20260530231947_InitialCreate` and it already covers every entity above — NO new migration is needed this phase.**

**Domain pattern (canonical):** `RentalCommand.Api/Services/Domain/ExpenseService.cs` implements `IExpenseService.CreateAsync(int portfolioId, CreateExpenseRequest request, CancellationToken ct) → Task<ExpenseResponse?>`; validates FKs via `_db.EnsurePropertyInPortfolioAsync` / `EnsureVendorInPortfolioAsync` (extension methods on `PortfolioScopeGuards`); broadcasts via `_dataUpdate.BroadcastEntityUpdateAsync(portfolioId, "Expense", entity.Id, response, ct)`. `CreateExpenseRequest` (`RentalCommand.Api/DTOs/ExpenseDtos.cs`): `int? PropertyId, int? VendorId, int? WorkOrderId, ScheduleECategory Category, string Description, ExpenseStatus Status, decimal Amount, DateTime IncurredAt, DateTime? DueDate, DateTime? PaidAt, bool BillableToOwner, string? Notes`. `ExpenseResponse.FromEntity(entity)` builds the response DTO. Controllers extend `AuthenticatedPortfolioControllerBase` (gives `GetPortfolioId()` from the JWT `portfolioId` claim and `GetUserId()` from `NameIdentifier`), use `[ApiController] [Route("api/v1/<entity>")] [Produces("application/json")]`, and the route does **NOT** include `{portfolioId}`. Service registrations live in `RentalCommand.Api/Extensions/ServiceCollectionExtensions.cs` `AddDomainServices()`.

**Engine:** `RentalCommand.Engine/Workers/EngineWorkerBase.cs` — abstract `string WorkerName`, `TimeSpan PollInterval`, `TimeSpan StepTimeout`, `Task<int> ExecuteCycleAsync(IServiceProvider scopedProvider, CancellationToken ct)`. `ExecuteAsync` creates a DI scope per cycle, links a `StepTimeout` CTS, logs the returned count, delays `PollInterval`. `OutboxDispatchWorker.cs` is the working template (resolves services from `scopedProvider`, queries, mutates, `SaveChangesAsync`). Workers register in `RentalCommand.Engine/Program.cs` via `builder.Services.AddHostedService<...>()`. **The Engine host has NO `ILlmProvider`/`IFileStorage`/`IDataUpdateService` registered yet** — Task 8 adds them.

**Web:** API client `web/src/lib/api/client.ts` exposes `api.get/post/patch/put/delete/upload(path, formData)` (paths relative to `/api/v1`, Bearer auth + refresh handled). SignalR client `web/src/lib/realtime/signalr.ts` + bridge `web/src/lib/realtime/invalidate.ts` map `entityType → query keys`. Toasts: `import { toast } from 'svelte-sonner'`. List page reference: `web/src/routes/(protected)/accounting/+page.svelte` (TanStack `createQuery`/`createMutation`, `data-testid` like `expense-row`). Route group `(protected)` is the authed shell. E2E: `web/e2e/*.spec.ts` with `web/e2e/helpers.ts` exporting `login(page)` (seeded `admin@rentalcommand.local` / `Admin123!`), `unique(prefix)`; `web/playwright.config.ts`.

**MCP:** `mcp/src/tools/ai-tools.ts` shows the tool pattern (`server.tool(name, desc, zodSchema, async handler)` using `api.post`/`api.get` and `getActivePortfolioId()`). `mcp/src/api-client.ts` is the REST client.

---

## Tasks

### Task 1: Extend `ILlmProvider.ExtractAsync` for routing + schema-driven extraction

**Phase 0 state:** `ILlmProvider` exists at `RentalCommand.Core/Interfaces/ILlmProvider.cs` with `ExtractAsync(byte[] imageOrPdfBytes, string prompt, CancellationToken ct)`. The current `prompt`-only signature can't tell the provider the content type (to route born-digital PDFs text-first) or carry the structured field schema/grounding context. Extend it now so every later task compiles against the final signature.

**Files:**
- Modify: `RentalCommand.Core/Interfaces/ILlmProvider.cs`

**Steps:**
- [ ] Change the `ExtractAsync` signature to:
  ```csharp
  Task<ExtractedFields> ExtractAsync(
      byte[] documentBytes,
      string contentType,                 // e.g. "image/jpeg", "application/pdf"
      string instructions,                // human-readable extraction instructions
      IReadOnlyList<ExtractionFieldSpec> fields,   // the schema the model must fill
      string? groundingContext = null,    // optional JSON of vendors/properties/units to match against
      CancellationToken ct = default);
  ```
  Keep `ChatAsync` unchanged.
- [ ] Add a new public record in the same file describing each expected field:
  ```csharp
  /// <summary>One field the model is asked to extract, used to build the tool schema.</summary>
  public sealed record ExtractionFieldSpec(
      string Name,            // JSON key, e.g. "vendor_name"
      string Type,            // "string" | "number" | "date" | "enum"
      string Description,     // guidance shown to the model
      bool Required = false,
      IReadOnlyList<string>? EnumValues = null);
  ```
- [ ] Leave `ExtractedFields` / `FieldExtraction` / `Box` exactly as they are (they are the return contract; do not rename).
- [ ] **Verify:** `dotnet build RentalCommand.Core` succeeds (it will still build because no concrete impl exists yet).
- [ ] **Commit:** "Core: extend ILlmProvider.ExtractAsync with contentType + field schema + grounding"

### Task 2: Receipt extraction schema + DTO

**Phase 0 state:** Nothing extraction-related exists in the Api project. Use `ScheduleECategory` (the real enum) for the category field.

**Files:**
- Create: `RentalCommand.Api/Scanning/ReceiptExtractionSchema.cs`
- Create: `RentalCommand.Api/Scanning/ExtractedReceiptDto.cs`

**Steps:**
- [ ] Create `ExtractedReceiptDto` (POCO the confirm flow deserializes from `ScanDraft.ExtractedFields`):
  ```csharp
  namespace RentalCommand.Api.Scanning;

  public sealed class ExtractedReceiptDto
  {
      public string? VendorName { get; set; }
      public decimal? Amount { get; set; }
      public DateTime? TransactionDate { get; set; }
      public RentalCommand.Core.Enums.ScheduleECategory? Category { get; set; }
      public string? Notes { get; set; }
  }
  ```
- [ ] Create `ReceiptExtractionSchema` exposing the instructions + the `ExtractionFieldSpec` list the provider/worker pass to `ExtractAsync`:
  ```csharp
  using RentalCommand.Core.Interfaces;

  namespace RentalCommand.Api.Scanning;

  public static class ReceiptExtractionSchema
  {
      public const string PromptId = "receipt-to-expense-v1";

      public const string Instructions =
          "You are extracting fields from a vendor receipt or invoice for a US residential-rental " +
          "bookkeeping system. Read the document and fill every field you can. For 'category', map the " +
          "expense to the closest IRS Schedule E category. Do NOT guess values that are not present.";

      public static IReadOnlyList<ExtractionFieldSpec> Fields { get; } = new[]
      {
          new ExtractionFieldSpec("vendor_name", "string", "The merchant / vendor / payee name.", Required: true),
          new ExtractionFieldSpec("amount", "number", "The grand total charged, as a decimal number (no currency symbol).", Required: true),
          new ExtractionFieldSpec("transaction_date", "date", "Date of the transaction in ISO 8601 (YYYY-MM-DD).", Required: true),
          new ExtractionFieldSpec("category", "enum", "Closest IRS Schedule E expense category.", Required: true,
              EnumValues: Enum.GetNames<RentalCommand.Core.Enums.ScheduleECategory>()),
          new ExtractionFieldSpec("notes", "string", "Any short free-text note (e.g. line-item summary). Optional."),
      };
  }
  ```
- [ ] **Verify:** `dotnet build RentalCommand.Api`.
- [ ] **Commit:** "Api: add Receipt→Expense extraction schema + DTO"

### Task 3: Implement `AnthropicLlmProvider` (Claude Messages API)

**Phase 0 state:** No concrete `ILlmProvider` exists; `AssistantConfig` exists but is unbound. This is the hardest task — the full HTTP body is inlined below; copy it.

**Files:**
- Create: `RentalCommand.Api/Scanning/AnthropicLlmProvider.cs`
- Modify: `RentalCommand.Api/Program.cs` (bind `AssistantConfig`, register the typed `HttpClient` + `ILlmProvider`)

**Steps:**
- [ ] In `Program.cs`, after the other `Configure<...>` calls, add:
  ```csharp
  builder.Services.Configure<RentalCommand.Core.Configuration.AssistantConfig>(
      builder.Configuration.GetSection(RentalCommand.Core.Configuration.AssistantConfig.SectionName));
  builder.Services.AddHttpClient<RentalCommand.Core.Interfaces.ILlmProvider, RentalCommand.Api.Scanning.AnthropicLlmProvider>(c =>
  {
      c.BaseAddress = new Uri("https://api.anthropic.com/");
      c.Timeout = TimeSpan.FromSeconds(90);
  });
  ```
- [ ] Create `AnthropicLlmProvider.cs` with the body below verbatim. Key points it must satisfy: model from `AssistantConfig.ModelId`; no-op deterministic fallback when `ApiKey` is null/empty (returns all fields `Confidence=0`, `ModelId="noop"`, `TokensUsed=0`, never makes a network call, logs once); born-digital PDF → extract text with the bundled `System.IO`/regex heuristic and send **text-only**; image or image-bearing/scanned PDF → send a **base64 document/image block**; force a single `tool_use` response so the model returns structured JSON **and a self-reported `confidence` 0–1 per field** (Claude exposes no calibrated confidence, so we ask for it in the tool schema); parse `input_tokens + output_tokens`.
  ```csharp
  using System.Net.Http.Headers;
  using System.Net.Http.Json;
  using System.Text;
  using System.Text.Json;
  using System.Text.RegularExpressions;
  using Microsoft.Extensions.Options;
  using RentalCommand.Core.Configuration;
  using RentalCommand.Core.Interfaces;

  namespace RentalCommand.Api.Scanning;

  /// <summary>
  /// Anthropic Claude Messages API implementation of <see cref="ILlmProvider"/>. Routes born-digital
  /// PDFs through text-only extraction (cheaper, more accurate) and everything else through a vision
  /// block. Forces a single tool_use response so the model returns structured JSON plus a self-reported
  /// 0–1 confidence per field. Falls back to a deterministic no-op (no network call, all confidence 0)
  /// when no API key is configured, so the app runs offline.
  /// </summary>
  public sealed class AnthropicLlmProvider : ILlmProvider
  {
      private const string AnthropicVersion = "2023-06-01";
      private const string ToolName = "record_extraction";

      private readonly HttpClient _http;
      private readonly AssistantConfig _config;
      private readonly ILogger<AnthropicLlmProvider> _logger;
      private bool _warnedNoKey;

      public AnthropicLlmProvider(HttpClient http, IOptions<AssistantConfig> config, ILogger<AnthropicLlmProvider> logger)
      {
          _http = http;
          _config = config.Value;
          _logger = logger;
      }

      public async Task<string> ChatAsync(string prompt, CancellationToken ct = default)
      {
          if (string.IsNullOrWhiteSpace(_config.ApiKey))
          {
              WarnNoKeyOnce();
              return string.Empty;
          }

          var body = new
          {
              model = _config.ModelId,
              max_tokens = 1024,
              messages = new[] { new { role = "user", content = prompt } }
          };
          using var resp = await SendAsync(body, ct);
          using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
          var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString();
          return text ?? string.Empty;
      }

      public async Task<ExtractedFields> ExtractAsync(
          byte[] documentBytes,
          string contentType,
          string instructions,
          IReadOnlyList<ExtractionFieldSpec> fields,
          string? groundingContext = null,
          CancellationToken ct = default)
      {
          // --- Deterministic no-op fallback (offline / unconfigured) ---
          if (string.IsNullOrWhiteSpace(_config.ApiKey))
          {
              WarnNoKeyOnce();
              return new ExtractedFields
              {
                  ModelId = "noop",
                  TokensUsed = 0,
                  Fields = fields.ToDictionary(
                      f => f.Name,
                      _ => new FieldExtraction { Value = string.Empty, Confidence = 0m })
              };
          }

          var isPdf = contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                      || (documentBytes.Length >= 4 && documentBytes[0] == 0x25 && documentBytes[1] == 0x50
                          && documentBytes[2] == 0x44 && documentBytes[3] == 0x46); // %PDF

          // Text-first routing for born-digital PDFs: if we can recover meaningful text, send it as text
          // (no vision tokens). Scanned/handwritten PDFs yield little text and fall through to vision.
          string? bornDigitalText = isPdf ? TryExtractPdfText(documentBytes) : null;

          var systemPrompt = new StringBuilder(instructions);
          if (!string.IsNullOrWhiteSpace(groundingContext))
          {
              systemPrompt.Append("\n\nKnown records in this portfolio you may match against (JSON):\n");
              systemPrompt.Append(groundingContext);
          }

          object userContent;
          if (bornDigitalText is { Length: > 40 })
          {
              userContent = new object[]
              {
                  new { type = "text", text = "Document text follows. Extract the fields.\n\n" + bornDigitalText }
              };
          }
          else
          {
              var mediaType = isPdf ? "application/pdf" : contentType;
              var blockType = isPdf ? "document" : "image";
              userContent = new object[]
              {
                  new
                  {
                      type = blockType,
                      source = new
                      {
                          type = "base64",
                          media_type = mediaType,
                          data = Convert.ToBase64String(documentBytes)
                      }
                  },
                  new { type = "text", text = "Extract the fields from the attached document." }
              };
          }

          var body = new
          {
              model = _config.ModelId,
              max_tokens = 1500,
              system = systemPrompt.ToString(),
              tools = new[] { BuildTool(fields) },
              tool_choice = new { type = "tool", name = ToolName },
              messages = new[] { new { role = "user", content = userContent } }
          };

          using var resp = await SendAsync(body, ct);
          var json = await resp.Content.ReadAsStringAsync(ct);
          return ParseToolResult(json, fields);
      }

      // ---- Anthropic tool schema: every field PLUS a sibling "<name>_confidence" number 0–1 ----
      private static object BuildTool(IReadOnlyList<ExtractionFieldSpec> fields)
      {
          var props = new Dictionary<string, object>();
          var required = new List<string>();
          foreach (var f in fields)
          {
              object schema = f.Type switch
              {
                  "number" => new { type = "number", description = f.Description },
                  "date"   => new { type = "string", description = f.Description + " (ISO 8601 date)" },
                  "enum"   => new { type = "string", @enum = f.EnumValues ?? Array.Empty<string>(), description = f.Description },
                  _        => new { type = "string", description = f.Description }
              };
              props[f.Name] = schema;
              props[f.Name + "_confidence"] = new
              {
                  type = "number",
                  description = $"Your calibrated confidence 0.0–1.0 that '{f.Name}' is correct. " +
                                "Use 1.0 only for values read verbatim and unambiguous; lower it when guessing or the source is unclear."
              };
              if (f.Required) { required.Add(f.Name); required.Add(f.Name + "_confidence"); }
          }

          return new
          {
              name = ToolName,
              description = "Return the extracted fields and a self-reported confidence for each.",
              input_schema = new { type = "object", properties = props, required = required.ToArray() }
          };
      }

      private static ExtractedFields ParseToolResult(string responseJson, IReadOnlyList<ExtractionFieldSpec> fields)
      {
          using var doc = JsonDocument.Parse(responseJson);
          var root = doc.RootElement;

          var result = new ExtractedFields
          {
              ModelId = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "",
              TokensUsed = root.TryGetProperty("usage", out var u)
                  ? (u.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0)
                    + (u.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0)
                  : 0
          };

          // Find the tool_use content block and read its "input" object.
          JsonElement input = default;
          var found = false;
          if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
          {
              foreach (var block in content.EnumerateArray())
              {
                  if (block.TryGetProperty("type", out var t) && t.GetString() == "tool_use"
                      && block.TryGetProperty("input", out input))
                  {
                      found = true;
                      break;
                  }
              }
          }

          foreach (var f in fields)
          {
              string value = "";
              decimal conf = 0m;
              if (found && input.ValueKind == JsonValueKind.Object)
              {
                  if (input.TryGetProperty(f.Name, out var v) && v.ValueKind != JsonValueKind.Null)
                  {
                      value = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
                  }
                  if (input.TryGetProperty(f.Name + "_confidence", out var c)
                      && c.ValueKind == JsonValueKind.Number)
                  {
                      conf = Math.Clamp(c.GetDecimal(), 0m, 1m);
                  }
                  // If the model returned a value but no confidence, treat as moderate rather than zero.
                  else if (value.Length > 0) { conf = 0.6m; }
              }
              result.Fields[f.Name] = new FieldExtraction { Value = value, Confidence = conf };
          }

          return result;
      }

      private Task<HttpResponseMessage> SendAsync(object body, CancellationToken ct)
      {
          var req = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
          {
              Content = JsonContent.Create(body)
          };
          req.Headers.TryAddWithoutValidation("x-api-key", _config.ApiKey);
          req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
          req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
          return SendChecked(req, ct);
      }

      private async Task<HttpResponseMessage> SendChecked(HttpRequestMessage req, CancellationToken ct)
      {
          var resp = await _http.SendAsync(req, ct);
          if (!resp.IsSuccessStatusCode)
          {
              var err = await resp.Content.ReadAsStringAsync(ct);
              _logger.LogError("Anthropic API error {Status}: {Body}", (int)resp.StatusCode, err);
              resp.EnsureSuccessStatusCode();
          }
          return resp;
      }

      // Minimal born-digital text recovery: pull readable text segments out of the PDF stream.
      // Good enough to detect text-PDFs and route them text-first; scanned PDFs return ~nothing
      // and fall through to vision. (A richer extractor can replace this later without touching callers.)
      private static string? TryExtractPdfText(byte[] bytes)
      {
          try
          {
              var raw = Encoding.Latin1.GetString(bytes);
              var matches = Regex.Matches(raw, @"\(((?:\\.|[^()\\])*)\)");
              if (matches.Count == 0) return null;
              var sb = new StringBuilder();
              foreach (Match mt in matches)
              {
                  var s = mt.Groups[1].Value.Replace("\\(", "(").Replace("\\)", ")").Replace("\\\\", "\\");
                  if (s.Trim().Length > 0) sb.Append(s).Append(' ');
              }
              var text = sb.ToString().Trim();
              return text.Length > 0 ? text : null;
          }
          catch { return null; }
      }

      private void WarnNoKeyOnce()
      {
          if (_warnedNoKey) return;
          _warnedNoKey = true;
          _logger.LogWarning(
              "AssistantConfig.ApiKey is not set — AnthropicLlmProvider runs in deterministic no-op mode " +
              "(extractions return empty values with confidence 0; no network calls are made).");
      }
  }
  ```
- [ ] **Verify:** `dotnet build RentalCommand.Api`.
- [ ] **Commit:** "Api: implement AnthropicLlmProvider (vision + text-first PDF routing + tool-output confidence + no-op fallback)"

### Task 4: Implement `FileStorage` (disk) + bind `UploadSettings`

**Phase 0 state:** `IFileStorage` contract exists; no impl. `UploadSettings` exists but is unbound. Implement the simple path/Guid-prefixed disk store that satisfies the existing interface; the `StoredFile` row is written by `ScanFileService` in Task 5 so this stays a pure blob store.

**Files:**
- Create: `RentalCommand.Api/Scanning/DiskFileStorage.cs`
- Modify: `RentalCommand.Api/Program.cs` (bind `UploadSettings`, register `IFileStorage`)
- Modify: `RentalCommand.Api/appsettings.json` and `RentalCommand.Api/appsettings.Development.json` (add `Upload` + `Assistant` sections)

**Steps:**
- [ ] Add to `appsettings.json` (and dev variant) a top-level `Upload` and `Assistant` section:
  ```json
  "Upload": {
    "BasePath": "./uploads",
    "MaxFileSizeBytes": 52428800,
    "AllowedMimeTypes": ["application/pdf", "image/jpeg", "image/png", "image/heic"]
  },
  "Assistant": {
    "ModelId": "claude-opus-4-8",
    "ApiKey": "",
    "ContextLength": 200000
  }
  ```
  (`ApiKey` empty by default so the no-op fallback is the default dev behavior; real key supplied via env `Assistant__ApiKey` or user-secrets.)
- [ ] In `Program.cs` add `builder.Services.Configure<UploadSettings>(builder.Configuration.GetSection(UploadSettings.SectionName));` and `builder.Services.AddScoped<IFileStorage, DiskFileStorage>();`.
- [ ] Create `DiskFileStorage`:
  ```csharp
  using Microsoft.Extensions.Options;
  using RentalCommand.Core.Configuration;
  using RentalCommand.Core.Interfaces;

  namespace RentalCommand.Api.Scanning;

  /// <summary>Local-disk <see cref="IFileStorage"/>. Stores files under a Guid-prefixed relative
  /// path inside <see cref="UploadSettings.BasePath"/>. The returned path is the relative key.</summary>
  public sealed class DiskFileStorage : IFileStorage
  {
      private readonly string _basePath;

      public DiskFileStorage(IOptions<UploadSettings> settings)
      {
          _basePath = string.IsNullOrWhiteSpace(settings.Value.BasePath) ? "./uploads" : settings.Value.BasePath;
          Directory.CreateDirectory(_basePath);
      }

      public async Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
      {
          var safe = SanitizeFileName(fileName);
          var key = $"{Guid.NewGuid():N}_{safe}";
          var full = Path.Combine(_basePath, key);
          await using (var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
          {
              await content.CopyToAsync(fs, ct);
          }
          return key;
      }

      public Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
      {
          var full = Path.Combine(_basePath, path);
          Stream s = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
          return Task.FromResult(s);
      }

      public Task DeleteAsync(string path, CancellationToken ct = default)
      {
          var full = Path.Combine(_basePath, path);
          if (File.Exists(full)) File.Delete(full);
          return Task.CompletedTask;
      }

      public static string SanitizeFileName(string name)
      {
          var trimmed = Path.GetFileName(name); // strips any directory components / traversal
          foreach (var c in Path.GetInvalidFileNameChars())
              trimmed = trimmed.Replace(c, '_');
          trimmed = trimmed.Replace("..", "_");
          return string.IsNullOrWhiteSpace(trimmed) ? "file" : trimmed;
      }
  }
  ```
- [ ] **Verify:** `dotnet build RentalCommand.Api`.
- [ ] **Commit:** "Api: implement DiskFileStorage + bind UploadSettings/Assistant config"

### Task 5: Upload validator + `ScanFileService` (writes the `StoredFile` row)

**Phase 0 state:** No validator and no `StoredFile`-writing service. `StoredFile` entity + DbSet exist. This service is the only writer of `StoredFile` rows for scans; it commits the DB row and the disk blob together.

**Files:**
- Create: `RentalCommand.Api/Scanning/FileUploadValidator.cs`
- Create: `RentalCommand.Api/Scanning/IScanFileService.cs`
- Create: `RentalCommand.Api/Scanning/ScanFileService.cs`

**Steps:**
- [ ] Create `FileUploadValidator` (static): `ValidateScanUpload(string? fileName, string contentType, long sizeBytes, UploadSettings settings) → (bool IsValid, string? Error)`. Checks: non-empty; `sizeBytes <= settings.MaxFileSizeBytes`; `contentType` is in `settings.AllowedMimeTypes` OR extension in `{.pdf,.jpg,.jpeg,.png,.heic}`; rejects `..` / path separators in `fileName`.
- [ ] Create `IScanFileService` with `Task<StoredFile> StoreAsync(int portfolioId, string targetEntityType, byte[] bytes, string fileName, string contentType, CancellationToken ct)`.
- [ ] Implement `ScanFileService(RentalCommandDbContext db, IFileStorage storage, IOptions<UploadSettings> settings, ILogger<ScanFileService>)`:
  - Validate via `FileUploadValidator`; throw `ArgumentException` on failure (controller maps to 400).
  - `var key = await storage.UploadAsync(new MemoryStream(bytes), fileName, contentType, ct);`
  - Create + save the `StoredFile` row: `PortfolioId`, `FileName = DiskFileStorage.SanitizeFileName(fileName)`, `FilePath = key`, `ContentType = contentType`, `FileSize = bytes.Length`, `EntityType = targetEntityType` (e.g. `"Expense"`), `EntityId = null` (re-keyed on confirm), `UploadedAt = DateTime.UtcNow`. `db.StoredFiles.Add(...)`, `await db.SaveChangesAsync(ct)`.
  - If `SaveChangesAsync` throws, best-effort `await storage.DeleteAsync(key, ct)` to avoid an orphan blob, then rethrow.
  - Return the `StoredFile`.
- [ ] Register in `ServiceCollectionExtensions.AddDomainServices()`: `services.AddScoped<IScanFileService, ScanFileService>();`.
- [ ] **Verify:** `dotnet build RentalCommand.Api`.
- [ ] **Commit:** "Api: add FileUploadValidator + ScanFileService (writes StoredFile row + blob)"

### Task 6: Implement `ScanService` (`CreateDraftAsync` + extend with confirm/reject)

**Phase 0 state:** `IScanService` in `RentalCommand.Core/Interfaces/IScanService.cs` declares only `CreateDraftAsync(int portfolioId, byte[] fileBytes, string contentType, string targetEntityType, CancellationToken ct)`. There is no impl, and no confirm/reject. This task adds confirm/reject to the interface and implements all three. `ScanService` lives in the Api project (it needs `IExpenseService`/`ExtractedReceiptDto`, both Api-side).

**Files:**
- Modify: `RentalCommand.Core/Interfaces/IScanService.cs` (add confirm/reject methods + a small result type)
- Create: `RentalCommand.Api/Scanning/ScanService.cs`
- Modify: `RentalCommand.Api/Extensions/ServiceCollectionExtensions.cs` (register `IScanService`)

**Steps:**
- [ ] Extend `IScanService` (keep `CreateDraftAsync` as-is) with:
  ```csharp
  /// <summary>Confirm a reviewed draft, creating the real record (Receipt→Expense in Phase 2).</summary>
  Task<ScanConfirmResult> ConfirmAndCreateAsync(
      int portfolioId, int draftId, int userId,
      string overridesJson, CancellationToken ct = default);

  /// <summary>Reject a draft; no record is created.</summary>
  Task<bool> RejectDraftAsync(
      int portfolioId, int draftId, int userId, string? reason, CancellationToken ct = default);
  ```
  and add `public sealed record ScanConfirmResult(bool Success, int? CreatedEntityId, string? Error);` in the same file.
- [ ] Implement `ScanService(RentalCommandDbContext db, IScanFileService files, IExpenseService expenses, IAuditTrailService audit, ILogger<ScanService> logger)`.
- [ ] `CreateDraftAsync`: call `files.StoreAsync(portfolioId, targetEntityType, fileBytes, $"scan-{DateTime.UtcNow:yyyyMMddHHmmss}", contentType, ct)`; create `ScanDraft { PortfolioId = portfolioId, FilePath = stored.FilePath, TargetEntityType = targetEntityType, Status = "Pending", CreatedAt = DateTime.UtcNow }`; `db.ScanDrafts.Add(draft); await db.SaveChangesAsync(ct);` return `draft`. (Extraction happens later in Engine — do NOT call the LLM here.)
- [ ] `ConfirmAndCreateAsync`:
  ```csharp
  var draft = await _db.ScanDrafts
      .FirstOrDefaultAsync(d => d.Id == draftId && d.PortfolioId == portfolioId, ct);
  if (draft is null) return new ScanConfirmResult(false, null, "Draft not found");
  if (draft.Status == "Confirmed") return new ScanConfirmResult(false, null, "Draft already confirmed");
  if (draft.TargetEntityType != "Expense")
      return new ScanConfirmResult(false, null, $"Unsupported target '{draft.TargetEntityType}'");

  // Start from the extracted fields, then apply the user's reviewed overrides (overrides win).
  var dto = BuildReceiptDto(draft.ExtractedFields);     // deserialize {field:{value,confidence}} -> ExtractedReceiptDto
  ApplyOverrides(dto, overridesJson);                    // merge user-edited values

  var request = new CreateExpenseRequest
  {
      Category = dto.Category ?? ScheduleECategory.Other,
      Description = string.IsNullOrWhiteSpace(dto.VendorName) ? "Scanned receipt" : dto.VendorName!,
      Status = ExpenseStatus.Pending,
      Amount = dto.Amount ?? 0m,
      IncurredAt = dto.TransactionDate ?? DateTime.UtcNow,
      BillableToOwner = false,
      Notes = dto.Notes,
  };
  var expense = await _expenses.CreateAsync(portfolioId, request, ct);
  if (expense is null) return new ScanConfirmResult(false, null, "Expense creation failed");

  // Re-key the uploaded file to the new Expense.
  var file = await _db.StoredFiles.FirstOrDefaultAsync(
      f => f.PortfolioId == portfolioId && f.FilePath == draft.FilePath, ct);
  if (file is not null) { file.EntityType = "Expense"; file.EntityId = expense.Id; }

  draft.Status = "Confirmed";
  draft.ConfirmedAt = DateTime.UtcNow;
  draft.ReviewedBy = userId.ToString();
  await _db.SaveChangesAsync(ct);

  await _audit.LogAsync(portfolioId, "Expense", expense.Id, AuditLogOperation.Created,
      userId: userId, changeReason: "Created from scan draft #" + draftId,
      newValues: draft.ExtractedFields, ct: ct);

  return new ScanConfirmResult(true, expense.Id, null);
  ```
  Implement the private helpers: `BuildReceiptDto(string? extractedFieldsJson)` parses the worker-written JSON shape `{"vendor_name":{"value":"...","confidence":0.9}, "amount":{...}, ...}` into `ExtractedReceiptDto` (map `transaction_date`→`DateTime`, `category`→`Enum.Parse<ScheduleECategory>` tolerant, `amount`→`decimal`); `ApplyOverrides(dto, overridesJson)` parses a flat `{"vendorName":"...","amount":12.34,"transactionDate":"2026-05-01","category":"Repairs","notes":"..."}` and overwrites any present key. Both must no-throw on missing/empty/`null` JSON.
- [ ] `RejectDraftAsync`: load draft (scoped), set `Status = "Rejected"`, `ReviewedAt = DateTime.UtcNow`, `ReviewedBy = userId.ToString()`, append `reason` into a `Notes`-like spot — since `ScanDraft` has no rejection field, store it as `changeReason` in an audit entry: `await _audit.LogAsync(portfolioId, "ScanDraft", draftId, AuditLogOperation.Rejected, userId: userId, changeReason: reason, ct: ct);` Save; return `true` (or `false` if not found).
- [ ] Register: `services.AddScoped<IScanService, ScanService>();` in `AddDomainServices()`.
- [ ] **Verify:** `dotnet build RentalCommand.Api`.
- [ ] **Commit:** "Api: implement ScanService (create draft, confirm→Expense + re-key + audit, reject)"

### Task 7: `ScanController` REST endpoints (`/api/v1` convention)

**Phase 0 state:** No scan controller. Follow the `ExpenseController` convention exactly: `[ApiController] [Route("api/v1/scans")] [Produces("application/json")]`, extend `AuthenticatedPortfolioControllerBase`, scope by `GetPortfolioId()` (NOT a `{portfolioId}` route param), `GetUserId()` for the actor.

**Files:**
- Create: `RentalCommand.Api/Controllers/ScanController.cs`
- Create: `RentalCommand.Api/DTOs/ScanDtos.cs`

**Steps:**
- [ ] In `ScanDtos.cs` define the **review-page data contract**:
  ```csharp
  namespace RentalCommand.Api.DTOs;

  /// <summary>One extracted field surfaced to the review UI.</summary>
  public sealed record ScanFieldDto(string Name, string Value, decimal Confidence);

  /// <summary>Draft as seen by the review page.</summary>
  public sealed record ScanDraftResponse(
      int Id, int PortfolioId, string TargetEntityType, string Status,
      string FileUrl, IReadOnlyList<ScanFieldDto> Fields,
      string? ModelId, int? TokensUsed, decimal? CostUsd,
      DateTime CreatedAt, DateTime? ReviewedAt, DateTime? ConfirmedAt);

  public sealed record ScanCreatedResponse(int DraftId, string Status, string FileUrl);
  public sealed record ConfirmScanRequest(string? OverridesJson);
  public sealed record RejectScanRequest(string? Reason);
  ```
  Add a static `ScanDraftResponse FromEntity(ScanDraft d)` that deserializes `d.ExtractedFields` (the worker JSON `{name:{value,confidence}}`) into the `Fields` list (empty list when null/Pending) and sets `FileUrl = $"/api/v1/scans/{d.Id}/file"`.
- [ ] `ScanController` endpoints:
  - `POST /api/v1/scans` — `[Consumes("multipart/form-data")]`, params `(IFormFile file, [FromForm] string targetEntityType, CancellationToken ct)`. Read bytes into a `byte[]` (`await file.CopyToAsync(ms, ct)`); `try { var draft = await _scan.CreateDraftAsync(GetPortfolioId(), bytes, file.ContentType, targetEntityType, ct); return CreatedAtAction(nameof(Get), new { id = draft.Id }, new ScanCreatedResponse(draft.Id, draft.Status, $"/api/v1/scans/{draft.Id}/file")); } catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }`.
  - `GET /api/v1/scans/{id:int}` (`Get`) — load `ScanDraft` scoped to `GetPortfolioId()`; `NotFound` if null; else `Ok(ScanDraftResponse.FromEntity(draft))`.
  - `GET /api/v1/scans?status=Pending&skip=0&take=50` — list scoped drafts (optional status filter), newest first, return `IReadOnlyList<ScanDraftResponse>`.
  - `GET /api/v1/scans/{id:int}/file` — load draft scoped; look up the `StoredFile` by `FilePath == draft.FilePath`; `var stream = await _files... ` — resolve `IFileStorage.DownloadAsync(draft.FilePath, ct)`; `return File(stream, storedFile.ContentType)`. 404 if missing.
  - `POST /api/v1/scans/{id:int}/confirm` — `[FromBody] ConfirmScanRequest body`; `var r = await _scan.ConfirmAndCreateAsync(GetPortfolioId(), id, GetUserId(), body?.OverridesJson ?? "{}", ct); return r.Success ? Ok(new { expenseId = r.CreatedEntityId }) : BadRequest(new { error = r.Error });`.
  - `POST /api/v1/scans/{id:int}/reject` — `[FromBody] RejectScanRequest body`; call `RejectDraftAsync`; `Ok()` / `NotFound()`.
  - Inject `IScanService` and `RentalCommandDbContext` + `IFileStorage` (for the file download / list reads). Controllers are auto-discovered by `app.MapControllers()` — no `Program.cs` change needed.
- [ ] **Verify:** `dotnet build RentalCommand.Api`.
- [ ] **Commit:** "Api: add ScanController (POST scan, GET draft/list/file, POST confirm/reject) on /api/v1/scans"

### Task 8: Register scan dependencies in the Engine host

**Phase 0 state:** `RentalCommand.Engine/Program.cs` registers only `RentalCommandDbContext`, `IMessagePublisher`, `INotificationChannel`, and `OutboxDispatchWorker`. The scan worker (Task 9) needs `ILlmProvider`, `IScanFileService`/`IFileStorage`, `IDataUpdateService`, `AssistantConfig`, `UploadSettings`, and the `ReceiptExtractionSchema`. The Anthropic provider, disk storage, and config classes live in `RentalCommand.Core`/`RentalCommand.Api`. **The Engine project must reference `RentalCommand.Api`** (or, cleaner, move `AnthropicLlmProvider`/`DiskFileStorage` into Core) — choose the simplest: add a project reference from Engine to Api.

**Files:**
- Modify: `RentalCommand.Engine/RentalCommand.Engine.csproj` (ProjectReference to `RentalCommand.Api`)
- Modify: `RentalCommand.Engine/Program.cs` (register provider, storage, config, data-update)
- Create: `RentalCommand.Engine/Services/EngineDataUpdateService.cs` (SignalR broadcast from Engine — see note)

**Steps:**
- [ ] Add to `RentalCommand.Engine.csproj`: `<ProjectReference Include="..\RentalCommand.Api\RentalCommand.Api.csproj" />`. Run `dotnet build RentalCommand.Engine` to confirm no circular reference (Api does not reference Engine — verify with `grep -r "RentalCommand.Engine" RentalCommand.Api/*.csproj`; expect no match).
- [ ] **SignalR from Engine:** the Engine is a separate process, so `DataUpdateService` (which needs an `IHubContext`) cannot reach the Api's in-memory hub. Implement `EngineDataUpdateService : IDataUpdateService` that POSTs the broadcast to the Api over HTTP — OR (simpler for now) write the update to the existing `OutboxMessage`/DB and let the web poll. **Chosen approach: HTTP call to a new internal Api endpoint.** Add `POST /api/v1/scans/{id}/notify-ready` to `ScanController` (API-key authed) that calls `IDataUpdateService.BroadcastEntityUpdateAsync(portfolioId, "ScanDraft", id, payload, ct)`. `EngineDataUpdateService` calls it with the configured `X-API-Key` (Engine reads `ApiKey`/base URL from config). If the HTTP notify fails, log and swallow (the web also has a bounded poll fallback — see Task 12).
- [ ] In `Program.cs`, register:
  ```csharp
  builder.Services.Configure<AssistantConfig>(builder.Configuration.GetSection(AssistantConfig.SectionName));
  builder.Services.Configure<UploadSettings>(builder.Configuration.GetSection(UploadSettings.SectionName));
  builder.Services.AddHttpClient<ILlmProvider, AnthropicLlmProvider>(c =>
  {
      c.BaseAddress = new Uri("https://api.anthropic.com/");
      c.Timeout = TimeSpan.FromSeconds(90);
  });
  builder.Services.AddScoped<IFileStorage, DiskFileStorage>();
  builder.Services.AddHttpClient<IDataUpdateService, EngineDataUpdateService>();
  builder.Services.AddHostedService<ScanProcessingWorker>();
  ```
  Add the matching `Assistant`/`Upload` sections + an `Api:BaseUrl` and `Api:ApiKey` to `RentalCommand.Engine/appsettings.json`.
- [ ] **Verify:** `dotnet build RentalCommand.sln`.
- [ ] **Commit:** "Engine: reference Api + register ILlmProvider/IFileStorage/IDataUpdateService for scanning"

### Task 9: `ScanProcessingWorker` on `EngineWorkerBase` + `AuditTrailService`

**Phase 0 state:** `EngineWorkerBase` + `OutboxDispatchWorker` exist as the template. `IAuditTrailService` contract exists; **no impl** — add it now (used by `ScanService` from Task 6 and any audit writes).

**Files:**
- Create: `RentalCommand.Engine/Workers/ScanProcessingWorker.cs`
- Create: `RentalCommand.Api/Services/AuditTrailService.cs`
- Modify: `RentalCommand.Api/Extensions/ServiceCollectionExtensions.cs` (register `IAuditTrailService`)

**Steps:**
- [ ] Implement `AuditTrailService(RentalCommandDbContext db) : IAuditTrailService` — `LogAsync` creates an `AuditLog` row from the args (`PortfolioId`, `EntityType`, `EntityId`, `Operation`, `UserId`, `ActorLabel`, `OldValues`, `NewValues`, `ChangeReason`, `IpAddress`, `CreatedAt = DateTime.UtcNow`) and `SaveChangesAsync`. Inspect the real `AuditLog` entity at `RentalCommand.Core/Entities/AuditLog.cs` and map only the columns it actually has. Register `services.AddScoped<IAuditTrailService, AuditTrailService>();` in `AddDomainServices()`.
- [ ] Create `ScanProcessingWorker` following `OutboxDispatchWorker` exactly:
  ```csharp
  using System.Text.Json;
  using Microsoft.EntityFrameworkCore;
  using Microsoft.Extensions.DependencyInjection;
  using Microsoft.Extensions.Logging;
  using RentalCommand.Api.Scanning;       // ReceiptExtractionSchema
  using RentalCommand.Core.Interfaces;
  using RentalCommand.Data;

  namespace RentalCommand.Engine.Workers;

  /// <summary>
  /// Polls Pending <see cref="Core.Entities.ScanDraft"/> rows, runs LLM extraction off the request
  /// path, writes the per-field {value,confidence} JSON + provenance back to the draft, flips Status
  /// to "Reviewing", and notifies the web via IDataUpdateService so the review page refreshes.
  /// On failure the draft is marked "Failed" so the UI can offer manual entry.
  /// </summary>
  public sealed class ScanProcessingWorker : EngineWorkerBase
  {
      private const int BatchSize = 10;

      protected override string WorkerName => "ScanProcessingWorker";
      protected override TimeSpan PollInterval => TimeSpan.FromSeconds(5);
      protected override TimeSpan StepTimeout => TimeSpan.FromSeconds(90);

      public ScanProcessingWorker(IServiceProvider serviceProvider, ILogger<ScanProcessingWorker> logger)
          : base(serviceProvider, logger) { }

      protected override async Task<int> ExecuteCycleAsync(IServiceProvider scoped, CancellationToken ct)
      {
          var db = scoped.GetRequiredService<RentalCommandDbContext>();
          var llm = scoped.GetRequiredService<ILlmProvider>();
          var storage = scoped.GetRequiredService<IFileStorage>();
          var dataUpdate = scoped.GetRequiredService<IDataUpdateService>();
          var logger = scoped.GetRequiredService<ILogger<ScanProcessingWorker>>();

          var pending = await db.ScanDrafts
              .Where(d => d.Status == "Pending")
              .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
              .Take(BatchSize)
              .ToListAsync(ct);
          if (pending.Count == 0) return 0;

          var processed = 0;
          foreach (var draft in pending)
          {
              ct.ThrowIfCancellationRequested();
              try
              {
                  // Read the stored bytes back from the blob store.
                  await using var stream = await storage.DownloadAsync(draft.FilePath, ct);
                  using var ms = new MemoryStream();
                  await stream.CopyToAsync(ms, ct);
                  var bytes = ms.ToArray();
                  var contentType = GuessContentType(draft.FilePath);

                  // Receipt→Expense only this phase.
                  var extracted = await llm.ExtractAsync(
                      bytes, contentType,
                      ReceiptExtractionSchema.Instructions,
                      ReceiptExtractionSchema.Fields,
                      groundingContext: null, ct);

                  // Persist {name:{value,confidence}} JSON + provenance.
                  var fieldJson = JsonSerializer.Serialize(extracted.Fields.ToDictionary(
                      kv => kv.Key,
                      kv => new { value = kv.Value.Value, confidence = kv.Value.Confidence }));
                  draft.ExtractedFields = fieldJson;
                  draft.ModelId = extracted.ModelId;
                  draft.TokensUsed = extracted.TokensUsed;
                  draft.CostUsd = EstimateCost(extracted.ModelId, extracted.TokensUsed);
                  draft.Status = "Reviewing";
                  draft.ReviewedAt = DateTime.UtcNow;
                  await db.SaveChangesAsync(ct);

                  await dataUpdate.BroadcastEntityUpdateAsync(
                      draft.PortfolioId, "ScanDraft", draft.Id,
                      new { draft.Id, draft.Status, draft.TargetEntityType }, ct);
                  processed++;
              }
              catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
              catch (Exception ex)
              {
                  logger.LogError(ex, "Scan extraction failed for draft {DraftId}", draft.Id);
                  draft.Status = "Failed";
                  draft.ReviewedAt = DateTime.UtcNow;
                  await db.SaveChangesAsync(ct);
                  await dataUpdate.BroadcastEntityUpdateAsync(
                      draft.PortfolioId, "ScanDraft", draft.Id,
                      new { draft.Id, draft.Status }, CancellationToken.None);
              }
          }
          return processed;
      }

      private static string GuessContentType(string path) =>
          Path.GetExtension(path).ToLowerInvariant() switch
          {
              ".pdf" => "application/pdf",
              ".png" => "image/png",
              ".heic" => "image/heic",
              _ => "image/jpeg"
          };

      // Rough Haiku-tier estimate; refined in Phase 5 budgeting. $0 when no real tokens (no-op).
      private static decimal EstimateCost(string modelId, int tokens) =>
          tokens == 0 ? 0m : Math.Round(tokens / 1_000_000m * 2.0m, 6);
  }
  ```
- [ ] **Verify:** `dotnet build RentalCommand.sln`.
- [ ] **Commit:** "Engine: add ScanProcessingWorker + Api: implement AuditTrailService"

### Task 10: Web — reusable `FileDrop` component

**Phase 0 state:** No upload component. `web/src/lib/components/` is the shared location. Use Svelte 5 runes + Tailwind; emit selection via a callback prop (Svelte 5 has no `createEventDispatcher` by convention here — use a `onselected` prop).

**Files:**
- Create: `web/src/lib/components/FileDrop.svelte`

**Steps:**
- [ ] Props: `{ onselected }: { onselected: (file: File) => void }`. Render a drag-drop zone (`ondragover`/`ondrop` with hover styling), a hidden `<input type="file" accept="application/pdf,image/*" capture="environment">` opened on click, selected file name + size, and a clear button. On `change`/`drop`, take the first file and call `onselected(file)`. Client-side MIME check is advisory only (warn via `toast.warning`, don't block).
- [ ] Add `data-testid="file-drop"` on the zone and `data-testid="file-drop-input"` on the input.
- [ ] **Verify:** `pnpm --dir web check` (Svelte/TS typecheck passes).
- [ ] **Commit:** "Web: add reusable FileDrop component"

### Task 11: Web — scan API client + `/scan` list page

**Phase 0 state:** No scan client/pages. Reference list page: `web/src/routes/(protected)/accounting/+page.svelte`. Routes go under `(protected)`.

**Files:**
- Create: `web/src/lib/api/scan.ts`
- Create: `web/src/routes/(protected)/scan/+page.svelte`

**Steps:**
- [ ] `scan.ts`: typed helpers over `api` (`web/src/lib/api/client.ts`):
  - `list(status?: string)` → `api.get<ScanDraftResponse[]>(\`/scans${status ? '?status=' + status : ''}\`)`
  - `get(id: number)` → `api.get<ScanDraftResponse>(\`/scans/${id}\`)`
  - `upload(file: File, targetEntityType: string)` → build `FormData` (`file`, `targetEntityType`), `api.upload<ScanCreatedResponse>('/scans', fd)`
  - `confirm(id, overridesJson)` → `api.post(\`/scans/${id}/confirm\`, { overridesJson })`
  - `reject(id, reason)` → `api.post(\`/scans/${id}/reject\`, { reason })`
  - Export TS types mirroring `ScanDraftResponse`/`ScanFieldDto`/`ScanCreatedResponse`.
- [ ] `/scan/+page.svelte`: `createQuery(['scans'], () => scan.list())`. Render `<FileDrop onselected={...}/>` at top; on select, `createMutation` calls `scan.upload(file, 'Expense')` and on success `goto(\`/scan/${res.draftId}\`)`. Below, a table of drafts: columns Status (badge: Pending=amber, Reviewing=blue, Confirmed=green, Failed/Rejected=red), TargetEntityType, CreatedAt, a "Review" link to `/scan/{id}`. Filter tabs All/Pending/Reviewing/Confirmed. `data-testid="scan-page"`, `scan-row`, `scan-upload`.
- [ ] **Verify:** `pnpm --dir web check`.
- [ ] **Commit:** "Web: add scan API client + /scan list page with upload"

### Task 12: Web — `/scan/[draftId]` review page (per-field confidence + confirm/reject)

**Phase 0 state:** No review page. SignalR bridge `web/src/lib/realtime/invalidate.ts` already invalidates query keys on `EntityUpdated`. Add `ScanDraft` to that map so the worker's broadcast refreshes the page; also keep a bounded poll as fallback (Engine→Api notify is best-effort).

**Files:**
- Create: `web/src/routes/(protected)/scan/[draftId]/+page.svelte`
- Modify: `web/src/lib/realtime/invalidate.ts` (add `ScanDraft` key mapping)

**Steps:**
- [ ] In `invalidate.ts`, add to `entityQueryKeys`: `ScanDraft: [['scans'], ['scan']]` and to `entityDetailKey`: `ScanDraft: 'scan'`.
- [ ] `+page.svelte`: read `draftId` from `$page.params`. `createQuery(['scan', draftId], () => scan.get(draftId), { refetchInterval: (q) => q.state.data?.status === 'Pending' ? 1500 : false })` (bounded poll only while Pending; SignalR flips it to Reviewing). Layout: left = scan preview (`<img src={fileUrl}>` for images, `<iframe>`/link for PDFs; `fileUrl` is `/api/v1/scans/{id}/file` proxied same-origin). Right = a form built from `data.fields`: for each field render label + input prefilled with `value`, and a confidence indicator: `confidence >= 0.8` normal, `0.5–0.79` muted/"Medium", `< 0.5` red border + "Low" + `autofocus` on the first low one. Category renders a `<select>` of `ScheduleECategory` names. Buttons: Confirm (`createMutation` → `scan.confirm(id, JSON.stringify(edited))`, on success `toast.success('Expense created')` + `goto('/accounting')`), Reject (prompt for reason → `scan.reject`, `toast` + back to `/scan`). When `status === 'Failed'`, show an error banner and still allow manual entry + Confirm.
- [ ] `data-testid`: `scan-review`, `scan-field-{name}`, `scan-confirm`, `scan-reject`.
- [ ] **Verify:** `pnpm --dir web check`.
- [ ] **Commit:** "Web: add /scan/[draftId] review page with per-field confidence + confirm/reject + SignalR refresh"

### Task 13: MCP `scan_document` tool

**Phase 0 state:** `mcp/src/tools/ai-tools.ts` shows the pattern; `mcp/src/api-client.ts` is the REST client. The REST upload is multipart; the MCP tool accepts base64 and posts it.

**Files:**
- Modify: `mcp/src/tools/ai-tools.ts`

**Steps:**
- [ ] Add `server.tool('scan_document', 'Upload a receipt/invoice (base64) to create a scan draft for human review.', { fileBase64: z.string(), fileName: z.string(), contentType: z.string(), targetEntityType: z.enum(['Expense']).default('Expense'), portfolioId: z.number().optional() }, handler)`.
- [ ] Handler: build a `FormData`/multipart body from the decoded base64 (Node `Buffer.from(fileBase64,'base64')`, `Blob`/`FormData`), POST to `/scans` via the api-client's upload path (extend `api-client.ts` with an `upload(path, formData)` if it lacks one), and return `{ draftId, status, fileUrl }`.
- [ ] **Verify:** `pnpm --dir mcp build` (tsc) succeeds; tool appears in the registered list.
- [ ] **Commit:** "MCP: add scan_document tool"

### Task 14: Tests & verify (lean — risky logic only)

**Phase 0 state:** xUnit test projects exist per the Phase-0 structure; `web/e2e/` Playwright is configured with `helpers.ts` (`login`, seeded admin). **No coverage target applies — write a few targeted tests on the genuinely risky logic only.**

**Files:**
- Create: `RentalCommand.Api.Tests/Scanning/AnthropicLlmProviderTests.cs`
- Create: `RentalCommand.Api.Tests/Scanning/ScanServiceTests.cs`
- Create: `web/e2e/scan.spec.ts`
- Create: `web/e2e/fixtures/receipt.png` (tiny sample image committed for the upload journey)

**Steps:**
- [ ] **AnthropicLlmProviderTests** (risky parsing + fallback only):
  - No-op fallback: construct with `AssistantConfig.ApiKey = null`; assert `ExtractAsync` returns every requested field with `Confidence == 0`, `ModelId == "noop"`, and (use a fake `HttpMessageHandler` that throws if called) **no HTTP call is made**.
  - Tool-result parsing: feed a canned Anthropic JSON response (a `tool_use` block whose `input` has `amount`, `amount_confidence`, `vendor_name`, `vendor_name_confidence`, and `usage.input_tokens`/`output_tokens`) through a stub `HttpMessageHandler`; assert the parsed `FieldExtraction` values + confidences match and `TokensUsed` is the sum.
- [ ] **ScanServiceTests** (the confirm path is the highest-risk business logic): with an in-memory/Sqlite `RentalCommandDbContext`, a real `ScanService`, a stub `IExpenseService` returning a known `ExpenseResponse`, and a stub `IAuditTrailService`:
  - `ConfirmAndCreateAsync` on a `Reviewing` Expense draft (with seeded `ExtractedFields` JSON + a matching `StoredFile`) → returns `Success`, the `StoredFile` is re-keyed (`EntityType=="Expense"`, `EntityId==expenseId`), the draft is `Confirmed`, and `IAuditTrailService.LogAsync` was called once with `AuditLogOperation.Created`.
  - `overridesJson` wins over extracted values (override the amount, assert the `CreateExpenseRequest.Amount` passed to the stub reflects the override).
  - `RejectDraftAsync` sets `Status=="Rejected"` and logs `AuditLogOperation.Rejected`.
- [ ] **Playwright `scan.spec.ts`** (reuse `helpers.ts` `login`): 2 journeys — (1) **upload + redirect:** `login`, go to `/scan`, upload `fixtures/receipt.png` via the `file-drop-input`, assert redirect to `/scan/<id>` and the review form (`scan-review`) renders (no-op mode yields empty/low-confidence fields, which is fine); (2) **confirm → expense:** fill the fields, click `scan-confirm`, assert success toast and landing on `/accounting`. Keep them resilient to the no-op extraction (don't assert specific extracted values).
- [ ] **Build / migration / auth smoke:**
  - `dotnet build RentalCommand.sln` — zero errors.
  - `dotnet ef database update --project RentalCommand.Data --startup-project RentalCommand.Api` — confirms the **existing** `20260530231947_InitialCreate` applies; **no new migration is added this phase** (verify with `dotnet ef migrations list` showing exactly one).
  - Run the API, then curl-smoke against the seeded admin (Origin header bypasses API-key auth per CLAUDE.md, but `/scans` requires a JWT — obtain a token via `/api/auth/login` with `admin@rentalcommand.local`/`Admin123!`, then):
    ```bash
    TOKEN=$(curl -sk https://localhost:5666/api/auth/login -H 'Content-Type: application/json' \
      -d '{"email":"admin@rentalcommand.local","password":"Admin123!"}' | jq -r .accessToken)
    # upload a receipt
    curl -sk -X POST https://localhost:5666/api/v1/scans -H "Authorization: Bearer $TOKEN" \
      -F "file=@web/e2e/fixtures/receipt.png" -F "targetEntityType=Expense"
    # list drafts (expect the new Pending/Reviewing one)
    curl -sk https://localhost:5666/api/v1/scans -H "Authorization: Bearer $TOKEN"
    # auth check: same call without the header -> 401
    curl -sko /dev/null -w '%{http_code}\n' https://localhost:5666/api/v1/scans
    ```
  - `dotnet test` (the two new test files pass); `pnpm --dir web exec playwright test e2e/scan.spec.ts`.
- [ ] **Commit:** "Tests: targeted unit tests (LLM fallback/parse, scan confirm) + scan Playwright journeys + smoke"

---

## Acceptance criteria

Phase 1+2 is complete when:

1. **Upload pipeline**
   - [ ] `DiskFileStorage` implements the existing `IFileStorage` and stores files Guid-prefixed under `UploadSettings.BasePath`; `ScanFileService` writes a `StoredFile` row (EntityType set, EntityId null) and the blob, cleaning up the blob if the DB write fails.
   - [ ] `FileUploadValidator` whitelists pdf/jpg/jpeg/png/heic and enforces `MaxFileSizeBytes`, rejecting path-traversal names.
   - [ ] `FileDrop.svelte` is reusable (drag-drop + camera input) and used by `/scan`.

2. **Scan draft & extraction**
   - [ ] `ILlmProvider.ExtractAsync` takes `contentType` + a field schema; `AnthropicLlmProvider` calls the Claude Messages API with a forced `tool_use`, parses values + self-reported per-field confidence, captures input+output tokens, routes born-digital PDFs text-first, and falls back to a deterministic no-op (no network call, confidence 0) when `AssistantConfig.ApiKey` is empty.
   - [ ] Model id comes from `AssistantConfig.ModelId` (not hardcoded).
   - [ ] `ScanProcessingWorker : EngineWorkerBase` polls `ScanDraft.Status == "Pending"`, writes `{name:{value,confidence}}` JSON + `ModelId`/`TokensUsed`/`CostUsd`, flips Status to `"Reviewing"`, and broadcasts `EntityUpdated("ScanDraft", id)`. (Status is a plain string; no enum, no new migration.)
   - [ ] Review UI shows the scan preview + per-field confidence (low fields highlighted + focused) with edit + Confirm/Reject.
   - [ ] Confirm calls `IExpenseService.CreateAsync(portfolioId, CreateExpenseRequest, ct)`, re-keys the `StoredFile` to the new Expense, marks the draft Confirmed, and writes an `IAuditTrailService.LogAsync` entry.

3. **Data handling & safety**
   - [ ] Human-confirm gate is non-bypassable (no auto-confirm path; the worker only proposes).
   - [ ] Token usage + estimated cost are recorded on each `ScanDraft`.
   - [ ] Deterministic no-op fallback verified by test.

4. **Quality**
   - [ ] `dotnet build RentalCommand.sln` passes; exactly one migration (`20260530231947_InitialCreate`) and it applies cleanly.
   - [ ] Targeted unit tests pass (LLM no-op + tool-parse; ScanService confirm→Expense+re-key+audit, overrides, reject).
   - [ ] 2 Playwright journeys pass (upload→redirect; confirm→expense) using the seeded admin.
   - [ ] `/api/v1/scans` returns 401 without a JWT and 404 for a missing draft.
   - [ ] **No `>80%` (or any) coverage mandate applies.**

5. **MCP parity**
   - [ ] `scan_document` tool is registered and posts a base64 upload to `/api/v1/scans`.

---

## Notes for executors

- **Hard spot #1 — vision-extraction tuning (Task 3).** The tool schema, the routing heuristic (born-digital vs scanned PDF), and especially the *self-reported confidence* are the parts where a Sonnet agent should slow down. Claude returns no calibrated confidence, so we ask the model for it in the tool input; treat the numbers as relative, not absolute. The `TryExtractPdfText` regex is a deliberately minimal "is there real text here?" detector — it is **not** a production PDF parser. If extraction quality on real receipts is poor, escalate to a stronger model via `AssistantConfig.ModelId` and consider sending the image at higher resolution; this tuning may warrant an Opus-class agent or a human review pass. Everything else in this plan is mechanical wiring a Sonnet agent can execute from the inlined code.
- **Hard spot #2 — cross-process SignalR from Engine (Task 8).** The Engine is a separate process and cannot share the Api's in-memory `IHubContext`. The plan uses an HTTP "notify-ready" callback from `EngineDataUpdateService` to the Api (API-key authed) plus a bounded client-side poll while a draft is `Pending`. If the HTTP callback proves flaky, the poll already covers correctness; do not block the worker on the notify (log-and-swallow). An executor who prefers can instead have the worker enqueue an `OutboxMessage`/DB flag the Api hub relays — keep whichever is simplest, but keep the poll fallback.
- **Do NOT create** `StoredFile`/`ScanDraft`/`OutboxMessage` entities, the `ScanDraftStatus`/`StoredFileEntityType` enums, a `LifecycleDbContext`, or any new migration — all of that already exists from Phase 0 (`ScanDraft.Status` is a `string`, `ExtractedFields` is a `jsonb string?`). Routes are `/api/v1/scans/*` scoped by `GetPortfolioId()`, never `/api/portfolio/{portfolioId}/...`.
