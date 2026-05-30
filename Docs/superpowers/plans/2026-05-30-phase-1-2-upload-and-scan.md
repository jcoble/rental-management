# Phase 1 + 2 — Upload Pipeline + Scan-to-Record Flagship — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Build the flagship upload-and-scan-to-record pipeline: generalize attachments to polymorphic storage, implement the async LLM-powered extraction worker, and ship Receipt→Expense as the first extraction target with a review UI that is 100% human-controlled.

**Architecture:** Phase 1 creates the file-handling plumbing (polymorphic `StoredFile` entity, filesystem-backed storage with Guid-prefixing, upload validator, reusable component); Phase 2 layers on the LLM extraction logic (real AnthropicLlmProvider in Core, async ScanProcessingWorker in Engine, ScanDraft model, review UI) with deterministic no-op fallback when API key is unset. Files are stored before DB commit; extraction runs off-request-path via Engine worker with SignalR notification to ready UI; every extract proposal must be human-confirmed before it becomes a real record. Token tracking and cost strategy (text-first routing, page-limiting, prompt caching, Haiku-tier) are explicit. MCP scan_document tool added for parity.

**Tech Stack:** .NET 10, PostgreSQL, EF Core, Svelte 5, SignalR, Anthropic .NET SDK (or typed HttpClient with official model), xUnit + Playwright.

**Depends on:** Phase 0 (4-project structure, Postgres, Identity/JWT, SignalR, RentalCommand namespace, IdentityDbContext<ApplicationUser>, Roles, AppSettings configuration).

---

## File / Project Structure

**New Entities (RentalCommand.Data/Entities):**
- `StoredFile.cs` — polymorphic attachment (int Id, int PortfolioId, string EntityType [Expense, Payment, Lease, Tenant, WorkOrder, Inspection, Vendor], int EntityId, string FileName, string StoragePath, long FileSize, int? Width, int? Height, string UploadedBy, DateTime UploadedAt, DateTime? DeletedAt for soft-delete); triggers audit on create/update.
- `ScanDraft.cs` — the proposal (int Id, int PortfolioId, string FilePath, string TargetEntityType, ScanDraftStatus Status [Pending, Reviewing, Confirmed, Rejected], JsonElement ExtractedFields {fieldName: {value, confidence:0-1, sourceBox?}}, string? LlmProvenance {model, prompt_id, token_usage}, DateTime CreatedAt, DateTime? ProcessedAt, string? ConfirmedByUserId, DateTime? ConfirmedAt, string? RejectionReason); FK to the record if confirmed (e.g., ExpenseId after confirmation).
- `OutboxMessage.cs` (Engine feature) — int Id, int PortfolioId, string MessageType, JsonElement Payload, bool Sent, int RetryCount, DateTime CreatedAt, DateTime? SentAt; reliable email/SMS queue.

**New Core Services (RentalCommand.Core/Services):**
- `ILlmProvider.cs` — async Task<ExtractAsync(IFormFile file, string extractionSchema, List<object> groundingContext, CancellationToken ct)` returns `{success: bool, fields: {fieldName: {value, confidence, sourceBox}}, model: string, promptId: string, tokenUsage: int}`. One implementation per provider (Anthropic now, swappable later).
- `IScanService.cs` — async Task<ScanDraft> CreatePendingDraftAsync(int portfolioId, string entityType, IFormFile file, ...); async Task<ScanDraft> ConfirmAndCreateAsync(int draftId, userId, ...); used by tests and Engine.

**New API / Config (RentalCommand.Api):**
- `UploadSettings.cs` — string BasePath (env UPLOADS_PATH, default "./uploads"), long MaxFileSizeBytes (default 50 MB).
- `FileUploadValidator.cs` — static methods ValidateUpload (checks extension whitelist: pdf, jpg, jpeg, png, heic; checks size; sanitizes filename).
- `StorageService.cs` — GenerateStoragePath(PortfolioId, EntityType, Guid), SaveFileAsync(...), DownloadAsync(...), DeleteOrphanAsync(...). Guid-prefixed paths; DB commit before disk (transaction ensures atomicity).
- `AnthropicLlmProvider.cs` — implements ILlmProvider; calls Anthropic API with vision block + forced JSON tool output; reads model from AssistantConfig; includes no-op fallback (returns mocked confidence=0 when ANTHROPIC_API_KEY unset).

**New API Endpoints (RentalCommand.Api/ScanEndpoints.cs):**
- `POST /api/portfolio/{portfolioId}/scan` (multipart) — store file, create Pending ScanDraft, return draft ID + immediate redirect to review UI (no extraction blocking request).
- `GET /api/portfolio/{portfolioId}/scan/{draftId}` — read draft + extracted fields (if ready).
- `POST /api/portfolio/{portfolioId}/scan/{draftId}/confirm` — validate user input, call IScanService.ConfirmAndCreateAsync, re-key StoredFile to Expense, return created Expense (calls existing POST /expenses endpoint logic).
- `POST /api/portfolio/{portfolioId}/scan/{draftId}/reject` — mark REJECTED, log reason.
- `GET /api/file/{fileId}` — download StoredFile (polymorphic, auth check PortfolioId match).

**Engine Worker (RentalCommand.Engine/Workers):**
- `ScanProcessingWorker : EngineWorkerBase` — polls for Pending ScanDrafts, calls ILlmProvider.ExtractAsync (with Receipt→Expense schema first), updates ScanDraft to Reviewing + extracted fields + provenance, broadcasts via IDataUpdateService.BroadcastAsync("portfolio-{id}", "ScanReady", draftId).

**UI Components (web/src):**
- `<FileDrop />` — reusable drag-drop + camera input component, attached to any form that needs file capture (used in expense-create, payment-create, lease-create, tenant-create, work-order-create, inspection-create, vendor-create; also desktop-scan and email-to-inbox surfaces).
- `ScanReviewPage.svelte` — per-field confidence display (high ≥0.8 normal, 0.5–0.79 muted, <0.5 highlighted + focus-first), thumbnail of scan, editable form for low-confidence fields, Confirm button (calls POST /confirm), Reject button.
- `ScanListPage.svelte` — portfolio's pending/reviewing/confirmed scans, filter by status + entity type.

**MCP Tool (mcp/src):**
- `scan_document(fileUrl, docType)` — MCP surface for external agents to trigger scans (calls REST endpoint).

---

## Tasks

### Task 1: Create Core LLM Provider Abstraction

**Files:**
- Create: `RentalCommand.Core/Services/ILlmProvider.cs`
- Create: `RentalCommand.Core/Dtos/LlmExtractionRequest.cs`
- Create: `RentalCommand.Core/Dtos/LlmExtractionResponse.cs`
- Create: `RentalCommand.Data/Entities/AssistantConfig.cs` (config entity, not yet used)
- Modify: `RentalCommand.Data/LifecycleDbContext.cs` (add DbSet<AssistantConfig>)

**Steps:**
- [ ] Define `ILlmProvider` with `Task<LlmExtractionResponse> ExtractAsync(string filePath, string extractionSchema, List<GroundingContext> groundingData, CancellationToken ct)` signature; return DTO includes success flag, extracted fields (dict of fieldName → {value, confidence, sourceBox}), model ID, prompt ID, token usage, error (if failed).
- [ ] Create DTO classes: `LlmExtractionRequest` (filePath, schema, groundingData); `LlmExtractionResponse` (success, fields, model, promptId, tokenUsage, error).
- [ ] Add `AssistantConfig` entity: Id, PortfolioId, LlmModelId (e.g., "claude-3-5-haiku-20241022"), MaxTokensPerExtraction, VisionModelEnabled, JsonOnly, CreatedAt, UpdatedAt. Default model from appsettings when unset per-portfolio.
- [ ] Add migration and update DbContext.
- [ ] **Verification:** Compile succeeds; ILlmProvider is injectable with Autofac/DI; DTO serialization round-trips.
- [ ] **Commit:** "Core: add ILlmProvider interface and extraction DTOs"

### Task 2: Implement Anthropic LLM Provider

**Files:**
- Create: `RentalCommand.Api/Services/AnthropicLlmProvider.cs`
- Create: `RentalCommand.Api/Configuration/AssistantSettings.cs` (config class for appsettings Anthropic section)
- Modify: `RentalCommand.Api/Program.cs` (register AnthropicLlmProvider, load AssistantSettings, log no-op fallback when key unset)

**Steps:**
- [ ] Create `AnthropicLlmProvider : ILlmProvider`. Constructor receives `IHttpClientFactory`, `ILogger`, `AssistantSettings` (holds ANTHROPIC_API_KEY, default model, max tokens).
- [ ] In `ExtractAsync`: if API key is unset, **return deterministic no-op** (all fields confidence=0, success=false, no call made; log once at startup). This is the hard requirement.
- [ ] If key is set: read file bytes, detect if PDF (check magic bytes 0x25504446) vs image. If PDF, attempt text extraction first (iText or PdfSharp) — if successful and text-only (no images), send **text only** to Claude. If PDF with images, handwritten, or already image, send **vision block**.
- [ ] Build Claude request: system prompt includes extraction schema as tool/function-calling JSON, ground against passed-in context (vendor list, property list, tenant list, unit list — as JSON in system prompt for caching); send `vision_type: "image"` + downscaled image (max 1500px) for efficiency; force `type: "json"` in response_format.
- [ ] Parse response, extract per-field confidence from any model-internal scoring (0.5–1.0 range); if missing, infer from response structure (exact string matches = 1.0, partial = 0.7, absent field = 0.3).
- [ ] Return `LlmExtractionResponse` with extracted fields, model ID, token usage (from response), prompt ID ("edi-receipt-v1" for now).
- [ ] **Verification:** Unit test with mocked HttpClient; test no-op fallback when key unset; test text-only PDF routing; test vision with image; verify token count is captured.
- [ ] **Commit:** "Api: implement AnthropicLlmProvider with vision + text-first routing + no-op fallback"

### Task 3: Generalize Attachment to Polymorphic StoredFile

**Files:**
- Create: `RentalCommand.Data/Entities/StoredFile.cs`
- Modify: `RentalCommand.Data/Entities/Attachment.cs` → mark as obsolete, don't delete yet (migration compatibility).
- Create: `RentalCommand.Data/Enums/StoredFileEntityType.cs` (Expense, Payment, Lease, Tenant, WorkOrder, Inspection, Vendor)
- Modify: `RentalCommand.Data/LifecycleDbContext.cs` (add DbSet<StoredFile>, configure polymorphic constraint, add AppId index for orphan cleanup).
- Create: Migration `AddStoredFilePolymorphic.cs`

**Steps:**
- [ ] Define `StoredFile` (int Id, int PortfolioId, StoredFileEntityType EntityType, int EntityId, string FileName, string OriginalFileName, string ContentType, long FileSize, string StoragePath, int? Width, int? Height, string? UploadedBy, DateTime UploadedAt, DateTime? DeletedAt, string? Metadata [JSON for future use]).
- [ ] Add fluent config: `HasKey(e => e.Id)`, `HasIndex(e => new {e.PortfolioId, e.EntityType, e.EntityId})` for query efficiency, `HasIndex(e => new {e.DeletedAt})` for orphan cleanup.
- [ ] Create enum `StoredFileEntityType` with all six targets.
- [ ] Add migration and apply.
- [ ] **Verification:** Migration applies cleanly; query `db.StoredFiles.Where(f => f.EntityType == StoredFileEntityType.Expense && f.EntityId == 123)` returns correct results.
- [ ] **Commit:** "Data: add polymorphic StoredFile entity + migration"

### Task 4: Create ScanDraft Entity

**Files:**
- Create: `RentalCommand.Data/Entities/ScanDraft.cs`
- Create: `RentalCommand.Data/Enums/ScanDraftStatus.cs` (Pending, Reviewing, Confirmed, Rejected)
- Modify: `RentalCommand.Data/LifecycleDbContext.cs` (add DbSet<ScanDraft>, configure shadow FK properties for nullable confirmed record).
- Create: Migration `AddScanDraft.cs`

**Steps:**
- [ ] Define `ScanDraft`: int Id, int PortfolioId, string FilePath, StoredFileEntityType TargetEntityType, int TargetEntityId [nullable, populated on confirm], ScanDraftStatus Status, JsonElement ExtractedFields, string? LlmModel, string? LlmPromptId, int? LlmTokenUsage, DateTime CreatedAt, DateTime? ProcessedAt, string? ConfirmedByUserId, DateTime? ConfirmedAt, string? RejectionReason.
- [ ] Add index: (PortfolioId, Status) for efficient polling; (PortfolioId, CreatedAt DESC) for list queries.
- [ ] Shadow properties for soft audit: UpdatedAt, UpdatedByUserId (optional, Phase 9).
- [ ] Add migration.
- [ ] **Verification:** Serialize/deserialize ExtractedFields (JsonElement) round-trip; status transitions are logical (Pending→Reviewing or Rejected, Reviewing→Confirmed or Rejected).
- [ ] **Commit:** "Data: add ScanDraft entity + status enum + migration"

### Task 5: Create Upload Settings & File Validator

**Files:**
- Create: `RentalCommand.Api/Configuration/UploadSettings.cs`
- Create: `RentalCommand.Api/Utilities/FileUploadValidator.cs`
- Modify: `appsettings.json` (add Uploads section with BasePath and MaxFileSizeBytes)

**Steps:**
- [ ] Define `UploadSettings`: BasePath (env UPLOADS_PATH, default "./uploads"), MaxFileSizeBytes (default 50 MB). Add validation in ctor or via FluentValidation.
- [ ] Define `FileUploadValidator` static class with public method `ValidateScanUpload(IFormFile? file) : (bool IsValid, string? ErrorMessage)`. Whitelist extensions: .pdf, .jpg, .jpeg, .png, .heic. Check file size. Check filename for path-traversal (..). Sanitize filename (remove .., /, \, invalid chars).
- [ ] Add method `SanitizeFileName(string name)` used by storage service.
- [ ] **Verification:** Test valid and invalid files; test size limit; test sanitization; compile.
- [ ] **Commit:** "Api: add UploadSettings and FileUploadValidator"

### Task 6: Create Storage Service & Guid-Prefixed Filesystem

**Files:**
- Create: `RentalCommand.Api/Services/FileStorageService.cs`
- Modify: `RentalCommand.Api/Program.cs` (register IFileStorageService / FileStorageService; configure UploadSettings from appsettings)
- Modify: `appsettings.json` and `appsettings.Development.json` (add Uploads:BasePath)

**Steps:**
- [ ] Define interface `IFileStorageService : SaveAsync(int portfolioId, string entityType, IFormFile file, CancellationToken ct) : Task<StoredFile>` (persists to DB and disk).
- [ ] Implement `FileStorageService(IDbContextFactory<RentalCommandDbContext>, UploadSettings, ILogger)`.
- [ ] In `SaveAsync`: (a) Validate file using `FileUploadValidator.ValidateScanUpload`. (b) Generate Guid + sanitized filename → `{portfolioId}/{entityType}/{guid}_{sanitizedName}`. (c) Create `StoredFile` record in-memory. (d) Within a DB transaction: insert StoredFile to get Id, write bytes to disk under computed path. If disk write fails, roll back transaction. (e) Return StoredFile DTO.
- [ ] In constructor, ensure BasePath directory exists.
- [ ] For image files (jpg, png, heic), read width/height via `Image.FromFile()` or `System.Drawing.Image`, store in StoredFile.
- [ ] Add `DownloadAsync(int fileId, int portfolioId)` — validate PortfolioId match, return file stream + content-type for HTTP response.
- [ ] Add `DeleteAsync(int fileId)` (soft-delete: set DeletedAt, don't touch disk yet; Phase 4 can add orphan cleanup worker).
- [ ] **Verification:** Test SaveAsync stores file and DB record atomically; test DownloadAsync returns correct bytes; test filename sanitization; test width/height capture for images.
- [ ] **Commit:** "Api: implement FileStorageService with Guid-prefixed filesystem + atomic DB-disk commit"

### Task 7: Create Receipt→Expense Extraction Schema

**Files:**
- Create: `RentalCommand.Api/Services/ExtractionSchemas/ReceiptExtractionSchema.cs`
- Create: `RentalCommand.Api/Dtos/ExtractedReceiptDto.cs`

**Steps:**
- [ ] Define `ReceiptExtractionSchema` static class. Output a JSON schema string describing fields: vendor_name (string), amount (decimal), transaction_date (DateTime ISO 8601), category (enum: Advertising, AutoTravel, CleaningMaintenance, Commissions, Insurance, LegalProfessional, ManagementFees, MortgageInterest, Repairs, Supplies, Taxes, Utilities, Depreciation, Other), line_items (optional, array of {description, qty, unit_price}), property_name_or_unit_hint (optional string to ground against property/unit list), notes (optional).
- [ ] Define `ExtractedReceiptDto` (POCO mirroring schema) for deserialization after Claude responds.
- [ ] Document expected grounding context: list of (id, name) for vendors, properties, units from the portfolio.
- [ ] **Verification:** Schema is valid JSON; it round-trips through Claude mock in tests.
- [ ] **Commit:** "Api: add Receipt extraction schema for Expense targeting"

### Task 8: Create Scan Service (Business Logic)

**Files:**
- Create: `RentalCommand.Api/Services/ScanService.cs`
- Modify: `RentalCommand.Api/Program.cs` (register IScanService / ScanService)

**Steps:**
- [ ] Define interface `IScanService`: 
  - `CreatePendingDraftAsync(int portfolioId, string targetEntityType, IFormFile file, string? userId, CancellationToken ct) : Task<ScanDraft>` — calls IFileStorageService.SaveAsync, creates ScanDraft with Status=Pending, stores FilePath from StoredFile.
  - `ConfirmAndCreateAsync(int draftId, int portfolioId, string userId, Dictionary<string, object>? fieldOverrides, CancellationToken ct) : Task<(bool Success, int? CreatedEntityId, string? Error)>` — takes confirmed ScanDraft, calls appropriate create endpoint (ExpenseService.CreateAsync for Receipt→Expense), re-keys StoredFile to new record, marks ScanDraft confirmed + ConfirmedByUserId + ConfirmedAt, returns created entity ID.
  - `RejectDraftAsync(int draftId, string reason, string userId, CancellationToken ct) : Task` — marks Status=Rejected, stores reason.
- [ ] Implement: inject IFileStorageService, RentalCommandDbContext, ILogger.
- [ ] In `ConfirmAndCreateAsync`, switch on TargetEntityType and call the appropriate service (Phase 2a: ExpenseService only; Phase 2b: PaymentService, Phase 2c: LeaseService, etc.). Pass extracted fields + field overrides to the create DTO.
- [ ] **Verification:** Unit test creates pending draft, then confirm-and-create; verify StoredFile is relinked; verify AuditLog entry is created.
- [ ] **Commit:** "Api: implement ScanService for draft lifecycle"

### Task 9: Create ScanController & REST Endpoints

**Files:**
- Create: `RentalCommand.Api/ScanEndpoints.cs` (or Controllers/ScanController.cs if following MVC pattern)
- Modify: `RentalCommand.Api/Program.cs` (MapScanEndpoints)

**Steps:**
- [ ] Endpoint `POST /api/portfolio/{portfolioId}/scan` (multipart file, targetEntityType query param):
  - Authorize user has PortfolioId from JWT claim.
  - Validate file.
  - Call `IScanService.CreatePendingDraftAsync`.
  - Return `{draftId, fileUrl, status}` (201 Created).
- [ ] Endpoint `GET /api/portfolio/{portfolioId}/scan/{draftId}`:
  - Return ScanDraft DTO (extracted fields, status, createdAt, processedAt, etc.).
- [ ] Endpoint `POST /api/portfolio/{portfolioId}/scan/{draftId}/confirm` (POST with optional fieldOverrides JSON in body):
  - Authorize user.
  - Call `IScanService.ConfirmAndCreateAsync`.
  - If success, return the created entity DTO (e.g., ExpenseDto).
  - If failure, return 400 with error.
- [ ] Endpoint `POST /api/portfolio/{portfolioId}/scan/{draftId}/reject` (POST with optional reason in body):
  - Call `IScanService.RejectDraftAsync`.
  - Return 200 OK.
- [ ] Endpoint `GET /api/file/{fileId}`:
  - Get StoredFile, validate PortfolioId from context.
  - Call `IFileStorageService.DownloadAsync`.
  - Return file stream with correct content-type and Content-Disposition header.
- [ ] **Verification:** Smoke test each endpoint; test auth (401 if no JWT); test 404 for nonexistent draft.
- [ ] **Commit:** "Api: add ScanController endpoints (POST scan, GET draft, POST confirm, POST reject, GET file download)"

### Task 10: Create ScanProcessingWorker in Engine

**Files:**
- Create: `RentalCommand.Engine/Workers/ScanProcessingWorker.cs`
- Create: `RentalCommand.Engine/Services/IScanProcessingService.cs`
- Create: `RentalCommand.Engine/Services/ScanProcessingService.cs`
- Modify: `RentalCommand.Engine/Program.cs` (register ScanProcessingWorker as BackgroundService)

**Steps:**
- [ ] Define `ScanProcessingWorker : EngineWorkerBase`. WorkerName = "ScanProcessing", PollInterval = 5 seconds, StepTimeout = 60 seconds (allow time for Claude call, likely 10–20s per receipt).
- [ ] In `ExecuteCycleAsync`: 
  - Query all ScanDrafts with Status=Pending.
  - For each, call `IScanProcessingService.ProcessDraftAsync`.
  - Return count processed.
- [ ] Define `IScanProcessingService.ProcessDraftAsync(ScanDraft draft, CancellationToken ct)` — calls `ILlmProvider.ExtractAsync(draft.FilePath, ReceiptExtractionSchema, groundingContext, ct)`, updates ScanDraft with extracted fields, status=Reviewing, LlmModel, LlmPromptId, LlmTokenUsage, ProcessedAt=now.
- [ ] After update, call `IDataUpdateService.NotifyAsync` (or broadcast via SignalR hub directly) to notify `portfolio-{portfolioId}` group that draft is ready: `{type: "scan-ready", draftId, status: "Reviewing", confidence: {...}}`.
- [ ] Error handling: if extraction fails, log error, set ScanDraft status=Rejected with error reason, notify anyway so UI can show error.
- [ ] **Verification:** Unit test with mocked ILlmProvider and DB context; verify Status transition Pending→Reviewing; verify SignalR notification broadcast; test error path.
- [ ] **Commit:** "Engine: add ScanProcessingWorker for async extraction with SignalR notification"

### Task 11: Create Svelte FileDrop Component

**Files:**
- Create: `web/src/lib/components/FileDrop.svelte`

**Steps:**
- [ ] Build a reusable `<FileDrop {entityType} on:selected={(file) => ...} />` component. Features:
  - Drag-drop zone (visual feedback on hover).
  - Click to open file picker.
  - Camera input on mobile (accept="image/*" with capture="environment").
  - Display selected file name + size; clear button.
  - Emit `on:selected` event with File object.
  - Validate MIME type client-side (warn if not supported; don't block).
- [ ] Optional: show thumbnail for images.
- [ ] Style with Tailwind; use `data-testid="file-drop"` for Playwright.
- [ ] **Verification:** Playwright test: drag file, verify selected event fires; click to pick file; mobile camera input shows on mobile viewport.
- [ ] **Commit:** "Web: add reusable FileDrop Svelte component"

### Task 12: Create ScanReviewPage

**Files:**
- Create: `web/src/routes/(protected)/(portal)/scan/[draftId]/+page.svelte`
- Create: `web/src/routes/(protected)/(portal)/scan/[draftId]/+page.ts` (load function)
- Create: `web/src/lib/api/scan.ts` (API client helpers)

**Steps:**
- [ ] In `+page.ts`: fetch ScanDraft (GET /api/portfolio/{id}/scan/{draftId}), loop-wait if Status=Pending (max 30s, poll every 1s), redirect to /scan/{draftId} again; once Status=Reviewing, show review form.
- [ ] In `+page.svelte`:
  - Header: "Review Receipt" with date + draft ID.
  - Left half: image/PDF thumbnail of uploaded scan (fetch via GET /api/file/{storedFileId}).
  - Right half: form with fields from ExtractedReceiptDto. For each field, show confidence as a visual indicator (bar or text: "High", "Medium", "Low"). Fields with confidence <0.5 are highlighted (red border) and focused first (tab order / autofocus).
  - Editable form fields (input[type=text], input[type=number], select for category, textarea for notes).
  - Buttons: Confirm (POST /confirm), Reject (POST /reject with optional reason).
  - Toast on success: "Expense created" + link to new expense.
  - Error toast if confirm fails.
- [ ] Use TanStack Query (useQuery / useMutation) for data + mutations.
- [ ] **Verification:** Playwright test: load draft, verify fields render; edit low-confidence field; click Confirm; verify toast + navigation to expense.
- [ ] **Commit:** "Web: add ScanReviewPage with per-field confidence display + edit + confirm flow"

### Task 13: Create ScanListPage

**Files:**
- Create: `web/src/routes/(protected)/(portal)/scan/+page.svelte`
- Create: `web/src/routes/(protected)/(portal)/scan/+page.ts`
- Modify: `web/src/lib/api/scan.ts` (add list endpoint)

**Steps:**
- [ ] Endpoint `GET /api/portfolio/{portfolioId}/scan?status=Pending&limit=50&offset=0` — paginated list of ScanDrafts filtered by status.
- [ ] In `+page.svelte`: list view with columns: EntityType, FileName, Status, CreatedAt, Actions (View/Confirm, Reject). Link "View" to [draftId] page. Show badge for status (Pending = yellow/amber, Reviewing = blue, Confirmed = green, Rejected = red).
- [ ] Filter tabs: All / Pending / Reviewing / Confirmed / Rejected.
- [ ] Polling or SignalR to refresh list in real-time when new drafts are processed.
- [ ] **Verification:** Playwright test: see Pending scan, click to review, come back to list, status changes.
- [ ] **Commit:** "Web: add ScanListPage with status filtering"

### Task 14: Wire SignalR for Scan-Ready Notifications

**Files:**
- Modify: `RentalCommand.Api/Hubs/DataUpdateHub.cs` (if not yet created, create per EdiPlatform pattern)
- Create: `RentalCommand.Api/Services/IDataUpdateService.cs`
- Create: `RentalCommand.Api/Services/DataUpdateService.cs`
- Modify: `RentalCommand.Api/Program.cs` (add SignalR, register DataUpdateService, MapHub<DataUpdateHub>)
- Modify: `RentalCommand.Engine/Services/ScanProcessingService.cs` (inject IDataUpdateService, broadcast notification)
- Modify: `web/src/lib/stores/signalr.ts` (handle "scan-ready" message)

**Steps:**
- [ ] Create `DataUpdateHub : Hub` similar to EdiPlatform's pattern. On connect, add user to groups `portfolio-{portfolioId}` and `user-{userId}`. Implement `Task SubscribeToEntity(string entityType, int entityId)` to add to group `{entityType}-{entityId}`.
- [ ] Define `IDataUpdateService.NotifyAsync(int portfolioId, string messageType, object payload, CancellationToken ct)` — broadcasts to `portfolio-{portfolioId}` group via IHubContext<DataUpdateHub>.
- [ ] In ScanProcessingService, after updating ScanDraft, call `IDataUpdateService.NotifyAsync(portfolioId, "scan-ready", {draftId, targetEntityType, confidence}, ct)`.
- [ ] In web, set up SignalR client (via `signalr.ts` store or similar) that listens for "scan-ready" messages and invalidates TanStack Query for `scanDraft-{draftId}` key. This triggers +page.ts loader re-fetch, which now sees Status=Reviewing + extracted fields.
- [ ] **Verification:** Integration test: upload scan, wait for ScanProcessingWorker cycle, verify DataUpdateHub broadcasts message, verify web listener updates UI.
- [ ] **Commit:** "Api + Engine + Web: wire SignalR for scan-ready notifications"

### Task 15: Create Receipt→Expense Confirmation Flow & Audit Trail

**Files:**
- Create: `RentalCommand.Api/Services/ExpenseService.cs` (or expand if exists)
- Modify: `RentalCommand.Api/Services/ScanService.cs` (ConfirmAndCreateAsync calls ExpenseService)
- Modify: `RentalCommand.Api/Services/AuditTrailService.cs` (or create, add Expense creation via scan audit)

**Steps:**
- [ ] Define `IExpenseService.CreateFromScanDraftAsync(ExtractedReceiptDto dto, int portfolioId, int? propertyId, string? userId) : Task<int expenseId>` — creates Expense entity using provided DTO fields; links to Vendor if vendor_name matched against existing Vendors; links to Property if property_name_or_unit_hint matched; sets Category using Schedule-E enum (from extracted category).
- [ ] In `ScanService.ConfirmAndCreateAsync`, for TargetEntityType=Expense: deserialize ExtractedReceiptDto from ScanDraft.ExtractedFields, apply fieldOverrides, call ExpenseService.CreateFromScanDraftAsync, get expenseId, update ScanDraft.TargetEntityId=expenseId, update StoredFile.EntityId=expenseId.
- [ ] Add AuditLog entry with action="ExpenseCreatedViaScan", entityType="Expense", entityId=expenseId, changes={sourceFile: storedFileName, extractedFields: {...}, confirmingUserId: userId}.
- [ ] **Verification:** Unit test: create ExpenseService with mocked vendor lookup; test CreateFromScanDraftAsync creates Expense with correct fields + category + vendor link; test that AuditLog is recorded.
- [ ] **Commit:** "Api: implement ExpenseService.CreateFromScanDraftAsync + audit trail for scan-to-record"

### Task 16: Add MCP scan_document Tool

**Files:**
- Modify: `mcp/src/tools/ai-tools.ts` (or create if separate)

**Steps:**
- [ ] Add tool `scan_document(fileUrl: string, targetEntityType: "expense" | "payment" | "lease" | "tenant"): Promise<{draftId, status}>`.
- [ ] Implementation: download file from URL (or accept base64), call REST API `POST /api/portfolio/{portfolioId}/scan`, return draftId + status.
- [ ] Add to MCP server's tool list with schema.
- [ ] **Verification:** MCP test: invoke scan_document with a real file URL or base64, verify draftId is returned.
- [ ] **Commit:** "MCP: add scan_document tool for external scan triggering"

### Task 17: Unit Test Suite (Phase 1+2 Core Logic)

**Files:**
- Create: `RentalCommand.Api.Tests/ScanServiceTests.cs`
- Create: `RentalCommand.Api.Tests/FileStorageServiceTests.cs`
- Create: `RentalCommand.Api.Tests/AnthropicLlmProviderTests.cs`
- Create: `RentalCommand.Engine.Tests/ScanProcessingWorkerTests.cs`

**Steps:**
- [ ] **ScanServiceTests:** (a) Test CreatePendingDraftAsync creates Pending draft + StoredFile. (b) Test ConfirmAndCreateAsync transitions Pending→Confirmed, creates Expense, re-links StoredFile. (c) Test RejectDraftAsync marks Rejected + reason.
- [ ] **FileStorageServiceTests:** (a) Test SaveAsync validates, stores file atomically (DB+disk), returns StoredFile with correct path. (b) Test DownloadAsync returns file stream. (c) Test filename sanitization. (d) Test width/height capture for images.
- [ ] **AnthropicLlmProviderTests:** (a) Test ExtractAsync with mocked HttpClient, verify Claude request format (vision block, tool schema, grounding context). (b) Test text-first routing for PDFs. (c) Test no-op fallback when API key unset. (d) Test confidence extraction.
- [ ] **ScanProcessingWorkerTests:** (a) Test ExecuteCycleAsync queries Pending drafts, calls ScanProcessingService, updates status to Reviewing. (b) Test error path (failed extraction → Rejected). (c) Test IDataUpdateService.NotifyAsync is called.
- [ ] Use FluentAssertions, Moq, and TransactionScope for DB rollback.
- [ ] **Verification:** `dotnet test` passes all tests; coverage >80%.
- [ ] **Commit:** "Tests: comprehensive unit tests for scan service, storage, LLM provider, worker"

### Task 18: Integration Test: End-to-End Scan Flow

**Files:**
- Create: `RentalCommand.IntegrationTests/ScanFlowTests.cs` (or E2E tests)

**Steps:**
- [ ] Set up a test with real RentalCommandDbContext + test DB (SQLite in-memory or Postgres fixture).
- [ ] Arrange: create test portfolio, properties, vendors, tenants. Upload a test receipt image.
- [ ] Act: POST /scan endpoint, poll GET /scan/{draftId} until Status=Reviewing (simulating ScanProcessingWorker).
- [ ] Assert: ScanDraft has extracted fields (vendor, amount, date, category). Call POST /confirm with field overrides. Assert Expense is created with correct fields, StoredFile.EntityId=expenseId, AuditLog entry exists.
- [ ] **Verification:** Test passes; Expense + StoredFile + AuditLog all present in DB after confirm.
- [ ] **Commit:** "Integration: end-to-end scan-to-record test (upload → process → confirm → expense)"

### Task 19: Playwright UI Test: Scan Review Flow

**Files:**
- Create: `RentalCommand.IntegrationTests/ScanReviewPlaywright.spec.ts` (or web/e2e/)

**Steps:**
- [ ] Test journey: (a) Login as test user. (b) Navigate to /scan. (c) Upload receipt image (via FileDrop). (d) Wait for redirect to /scan/{draftId}. (e) Verify form fields render with confidence indicators. (f) Edit a low-confidence field. (g) Click Confirm. (h) Verify toast + redirect to new Expense. (i) Verify Expense details match extracted + edited fields.
- [ ] Use Playwright's `page.uploadFile()` or drag-drop simulation. Test on desktop + mobile viewports.
- [ ] **Verification:** Test passes end-to-end in headless mode.
- [ ] **Commit:** "E2E: Playwright test for full scan-review-confirm journey"

### Task 20: Documentation & Data Handling Posture

**Files:**
- Create: `Docs/Data-Handling-Posture.md`
- Modify: `README.md` (add Phase 1+2 section)

**Steps:**
- [ ] Write **Data Handling Posture** document covering:
  - **Consent:** Images and PDFs containing PII (tenant name, address, ID) are sent to Anthropic Claude API. User must acknowledge before first scan upload.
  - **Retention:** Claude API docs state images are retained for 30 days for abuse detection, then deleted. No training on user data. Link to Anthropic privacy policy.
  - **Model & Token Tracking:** Model ID and token usage are logged per ScanDraft for cost tracking and audit.
  - **Deterministic Fallback:** When ANTHROPIC_API_KEY is unset, extraction returns mocked zero-confidence results; no external calls are made. Operator can run offline.
  - **Compliance:** Fair Housing + FCRA compliance enforced at extraction schema level (e.g., no race/ethnicity fields extracted from ID). User confirm gate is the legal control (human-in-the-loop).
- [ ] Add to README: "Phase 1+2: Upload & Scan (Flagship)" with overview + data-handling summary.
- [ ] **Verification:** Document is clear, complete, and references Anthropic policy.
- [ ] **Commit:** "Docs: add Data-Handling-Posture for LLM extraction + scan-to-record"

### Task 21: Build & Migration Verification

**Files:**
- Modify: `RentalCommand.sln` (ensure all projects compile)
- Run: `dotnet ef migrations add` for Phase 1+2 changes (StoredFile, ScanDraft, OutboxMessage)

**Steps:**
- [ ] Run `dotnet build RentalCommand.sln` — should pass with zero warnings.
- [ ] Run migrations: `dotnet ef database update` on test DB — all migrations apply cleanly.
- [ ] Verify DB schema: `StoredFile`, `ScanDraft`, `OutboxMessage` tables exist with correct columns + indexes.
- [ ] Smoke test: create a ScanDraft record via DbContext, query it back, verify all properties serialize/deserialize correctly.
- [ ] **Verification:** Build passes; migrations apply; DB matches expected schema.
- [ ] **Commit:** "Build: compile Phase 1+2, apply migrations to test DB"

### Task 22: Acceptance Test: Receipt Scan Capability

**Files:**
- Create: `Docs/Acceptance-Tests.md` (or expand existing)

**Steps:**
- [ ] Define acceptance criteria (to verify before closing Phase 1+2):
  - [ ] A user can upload a receipt photo or PDF via the UI.
  - [ ] File is stored in `{portfolioId}/{Expense}/{guid}_{filename}` on disk and in StoredFile table.
  - [ ] Within 30 seconds, the LLM extraction completes (or deterministic mock fires if no API key) and the ScanDraft status changes to Reviewing.
  - [ ] Review UI displays extracted vendor, amount, date, category with per-field confidence indicators.
  - [ ] User can edit low-confidence fields.
  - [ ] User clicks Confirm; new Expense is created with extracted + edited data, StoredFile is re-keyed to ExpenseId, AuditLog records the scan-to-expense conversion.
  - [ ] If user clicks Reject, ScanDraft is marked Rejected, no Expense is created.
  - [ ] Multiple users uploading simultaneously does not cause race conditions (concurrency tested).
  - [ ] No API key set: extraction mocks with zero confidence, user sees warning but can still confirm (to test without Claude access).
- [ ] Document test data: sample receipt image (PDF + photo) to use.
- [ ] **Verification:** All criteria pass on staging or test environment.
- [ ] **Commit:** "Docs: Phase 1+2 acceptance criteria + manual test checklist"

---

## Acceptance Criteria

Phase 1+2 is complete when:

1. **File Upload Pipeline (Phase 1):**
   - [ ] `StoredFile` is the single polymorphic entity for attachments to Expense, Payment, Lease, Tenant, WorkOrder, Inspection, Vendor.
   - [ ] Files are stored Guid-prefixed in `{BasePath}/{PortfolioId}/{EntityType}/{guid}_{sanitizedName}`.
   - [ ] `FileUploadValidator` whitelists pdf, jpg, jpeg, png, heic; enforces size cap.
   - [ ] DB-commit-before-disk atomicity is enforced (transaction test verifies this).
   - [ ] FileDrop Svelte component is reusable and included in all entity-create forms that accept files.

2. **Scan Draft & Extraction (Phase 2):**
   - [ ] `ILlmProvider` interface exists in Core; `AnthropicLlmProvider` implements it with vision + text-first routing + no-op fallback.
   - [ ] Model ID is configurable via `AssistantConfig.LlmModelId` (not hardcoded).
   - [ ] `ScanDraft` entity stores Status (Pending→Reviewing→Confirmed/Rejected), extracted fields as JSON, per-field confidence, LLM provenance (model, promptId, tokenUsage).
   - [ ] `ScanProcessingWorker` polls for Pending drafts, calls ILlmProvider.ExtractAsync, updates ScanDraft to Reviewing, broadcasts via SignalR.
   - [ ] Receipt→Expense extraction schema is defined and tested. Extracted fields include vendor, amount, date, category, optional line items, property hint.
   - [ ] Review UI displays scan + form with confidence-based field highlighting + edit + Confirm/Reject.
   - [ ] Confirm flow: calls ExpenseService.CreateFromScanDraftAsync, creates Expense, re-links StoredFile, records AuditLog.

3. **Data Handling & Safety:**
   - [ ] Consent acknowledgment is shown to user before first scan (in onboarding or on /scan page).
   - [ ] Data-Handling-Posture document explains image retention (30 days), no training, model transparency.
   - [ ] Deterministic no-op fallback when ANTHROPIC_API_KEY unset (verified by test).
   - [ ] Token usage is recorded on ScanDraft for per-account budgeting (Phase 5 can enforce limits).
   - [ ] Human-confirm gate is non-bypassable (no "auto-confirm-all" shortcut).

4. **Quality & Coverage:**
   - [ ] Build passes, zero warnings.
   - [ ] Migrations apply cleanly to test DB.
   - [ ] Unit tests cover ScanService, FileStorageService, AnthropicLlmProvider, ScanProcessingWorker; >80% coverage.
   - [ ] Integration test covers end-to-end (upload → process → confirm → expense).
   - [ ] Playwright test covers UI flow (upload → review → confirm → toast + expense link).
   - [ ] All endpoints have 401 auth check + 404 for missing resource.
   - [ ] Concurrent uploads do not race (lock-based if needed, or verified via test).

5. **MCP Parity:**
   - [ ] `scan_document` tool is available in MCP server and callable from external agents.

6. **Documented:**
   - [ ] Data-Handling-Posture is complete and links Anthropic privacy policy.
   - [ ] Acceptance criteria checklist is in place.
   - [ ] Phase 1+2 overview is in README.

---

## Plan Self-Review Notes

**Spec Sections Covered:**
- §4 (The spine: Capture → Draft → Confirm) — Receipt→Expense first; schema, extraction, review UI, confirm gate, re-keying, audit.
- §6.1 (What gets captured, what gets saved) — StoredFile storage, ScanDraft model, Receipt→Expense extraction.
- §9 (Cross-cutting) — Audit trail on Confirm, human-confirm gate (non-bypassable), LLM data-handling posture explicit.

**Key Decisions Baked In:**
- **Receipt-first ship order:** Simplest, lowest risk, most demonstrable. Check→Payment and Lease→Lease follow in Phase 2b+c.
- **Async extraction off request path:** ScanProcessingWorker polls, prevents request timeout on slow Claude calls (vision is inherently 10–20s).
- **SignalR for real-time UI update:** Avoids polling loops; UI is notified the moment ScanDraft.Status changes to Reviewing.
- **Polymorphic StoredFile from day one:** Slight up-front complexity but avoids painful retrofit when we add Lease, Tenant, etc. attachments in Phase 2b+c.
- **Deterministic no-op fallback:** Operator can develop + test without API key; no surprises in production when key is misconfigured.
- **Per-field confidence + low-confidence highlighting:** Builds trust (user sees why a field is proposed), reduces false-confirms. Confidence is inferred from Claude response structure + field presence.

**Risk Mitigation:**
- **LLM latency:** Async worker + SignalR notification. Max 30s wait in UI; if exceeds, show "taking longer" message and allow manual form entry fallback.
- **File storage race conditions:** Guid + timestamp prefix ensures no collisions. Atomic DB-disk transaction via EF Core transaction scope.
- **PII in Claude calls:** Data-handling posture document + consent gate + no-training clause from Anthropic terms. Encrypted TLS to Claude API.
- **Operator trust in extracted data:** Per-field confidence + highlight + forced manual confirm — low-confidence fields are always reviewed by human.
- **Test data:** Create small sample receipt (PDF + photo) to seed tests; add to repo under tests/fixtures/.

**Phase 2a (just Receipt→Expense), 2b (Check→Payment), 2c (Lease→Lease, ID→Tenant):**
- This plan covers the full foundation for all three. In Phase 2a, deploy Receipt extraction only (TargetEntityType filter). In 2b/c, reuse ILlmProvider + ScanProcessingWorker; only add new extraction schemas + confirmation flows.

**Handoff to Phase 2a Implementation:**
- Phase 1 tasks (1–6) create the plumbing and can ship independently (file upload + storage without extraction). Phase 2 tasks (7–22) add the LLM + extraction + review. Suggest Phase 1 merge to main as a stable base before Phase 2 development starts, so parallel work on Phase 3 (Real AI core) can begin.

---

**Next Phase:** Phase 2b (Check→Payment extraction) reuses infrastructure; Phase 3 (Real AI core + Daily Briefing + Q&A) builds the second-biggest sales demo.
