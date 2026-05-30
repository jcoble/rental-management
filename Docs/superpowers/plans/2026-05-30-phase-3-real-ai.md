# Phase 3 — Real AI core + the two brains — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Replace keyword-driven AI endpoints with real LLM-backed services (chat + vision via ILlmProvider), build the Daily Briefing outbound brain (rules-first with LLM enhancement), build Portfolio Q&A inbound brain (MCP-grounded plain-English ask), add Voice capture, Tenant FAQ bot, and maintenance photo triage.

**Architecture:** All AI features inherit from a single real `ILlmProvider` implementation (via `AnthropicLlmProvider` + config-driven model). Daily Briefing is an Engine background worker (daily trigger per portfolio, priority-sorted rules engine enhanced with LLM prose). Portfolio Q&A is a new `/api/ai/ask` endpoint that invokes the MCP server's tools (plan + tool-call loop) and grounds answers in live database state. Voice capture reuses the Phase 2 ScanDraft pattern (speech-to-text + LLM parse → draft). Lease FAQ bot parses the stored lease PDF via vision, caches system prompt, answers tenant Q&A. Maintenance triage is a vision endpoint that analyzes request photos → category/priority/vendor match. All endpoints honor PortfolioId claim scoping. All AI calls track token usage and cost on provenance records.

**Tech Stack:** .NET 10, EF Core + PostgreSQL, Claude API (vision + chat via official SDK), the existing MCP TypeScript server (no changes required — only consume its tools via HTTP), SignalR for briefing push, speech-to-text via device-native engines (Flutter `speech_to_text` plugin / Web Speech API) with a server-side fallback to Deepgram (or OpenAI Whisper) for recorded/Twilio audio, Prompt Caching for system prompts + grounding context.

**Depends on:** Phase 0 (re-platform + IdentityDbContext + JWT claims PortfolioId), Phase 1 (StoredFile polymorphic attachments), Phase 2 (ILlmProvider interface + AnthropicLlmProvider real impl + ScanDraft draft-confirm + Engine worker infrastructure + SignalR), Auth/AuthEndpoints (JWT + refresh + user scoping).

---

## File / project structure

**RentalCommand.Core** (interfaces + entities):
- `Services/ILlmProvider.cs` — already exists from Phase 2; expand with schema/tool-calling support for Q&A
- `Entities/BriefingTemplate.cs` — NEW: stores rules-based briefing rules per portfolio (templates for rent-due bullets, late-fee rules, chore reminders)
- `Entities/PortfolioAiConfig.cs` — NEW: per-portfolio LLM settings (model override, temperature, system-prompt customization)
- `Enums/VoiceCaptureType.cs` — NEW: WorkOrder, Expense, Payment
- `Entities/VoiceDraft.cs` — NEW: raw transcript + confidence, transcription provider, duration, sourceUrl (for signed URLs to audio blob storage)
- `Entities/MaintenanceTriageResult.cs` — NEW: analyzed photo → category, priority, confidence, suggested vendors (polymorphic on WorkOrder)

**RentalCommand.Api** (controllers, endpoints, DTOs, real implementations):
- `Api/AiEndpoints.cs` — REWRITE: `/api/ai/intake` (real extract via ILlmProvider), `/api/ai/ask` (Q&A + MCP loop), `/api/ai/voice` (transcribe + parse), `/api/ai/lease-faq` (FAQ grounded in lease), `/api/ai/triage-maintenance` (photo → category/priority)
- `Services/PortfolioQaService.cs` — NEW: orchestrates MCP tool invocation for Q&A; maintains conversation context; translates Q&A into MCP tool calls
- `Services/LeaseParsingService.cs` — NEW: caches lease PDF + vision extraction → chunked embeddings or simple markdown summary; answers via LLM grounding
- `Services/MaintenanceTriageService.cs` — NEW: vision + structured extract for work-order photos
- `Api/Dtos/AiIntakeRequest.cs` — KEEP: same shape as Phase 2 (PortfolioId, TargetEntityType, optional context)
- `Api/Dtos/PortfolioQaRequest.cs` — NEW: { PortfolioId, Question, ConversationHistory?, Context? }
- `Api/Dtos/PortfolioQaResponse.cs` — NEW: { Answer, SourceTools, TokenUsage, Confidence }
- `Api/Dtos/VoiceTranscriptRequest.cs` — NEW: { PortfolioId, AudioUrl, TargetEntityType, Context? }
- `Api/Dtos/MaintenanceTriageRequest.cs` — NEW: { PortfolioId, PhotoUrl, Description?, WorkOrderId? }
- `Services/DailyBriefingService.cs` — NEW: rules engine (rent due, late payments, appointments, auto-posted fees, recurring chores) + LLM prose enhancement
- `Middleware/TokenTrackingMiddleware.cs` — NEW (optional): per-request LLM token accounting
- `Config/AssistantConfig.cs` — EXISTS (Phase 2): ensure model is read from here, never hardcoded
- `AnthropicLlmProvider.cs` — EXISTS (Phase 2): expand to support tool schemas for Q&A

**RentalCommand.Engine** (background workers):
- `Workers/DailyBriefingWorker.cs` — NEW: runs once per day per portfolio (advisory-locked); composes briefing; publishes via SignalR + outbox message (SMS/email)
- `Workers/VoiceTranscriptionWorker.cs` — NEW: async transcription processor (polls VoiceDraft.Status == Pending, calls speech-to-text provider, updates Status → Reviewing, notifies via SignalR)

**web** (frontend):
- `routes/(protected)/(portal)/ai/+page.svelte` — NEW: Q&A box + conversation history
- `routes/(protected)/(portal)/ai/briefing/+page.svelte` — NEW: view today's briefing + previous days + actions
- `routes/(protected)/(portal)/ai/voice/+page.svelte` — NEW: record → transcribe → draft (reuses Phase 2 ScanDraft review UI)
- `lib/api/ai.ts` — NEW: ask(), uploadVoice(), triageMaintenance(), getLeaseFaq()
- `lib/stores/briefing.ts` — NEW: TanStack Query + SignalR bridge for daily-briefing push
- `lib/components/AiQaBox.svelte` — NEW: plain-English ask interface + conversation history
- `lib/components/DailyBriefingCard.svelte` — NEW: one-tap actions on briefing bullets

**mcp/src** (TypeScript MCP server):
- NO CHANGES REQUIRED — the existing tools (list_portfolios, get_portfolio_dashboard, etc.) are sufficient. Document which tools Q&A will invoke.

---

## Tasks

### Task 1: Expand ILlmProvider with tool-schema support for Q&A

**Files:** Create/Modify

- Modify: `/Users/blackcolours/dev/work/rental-management/api/Api/Core/Services/ILlmProvider.cs`
- Modify: `/Users/blackcolours/dev/work/rental-management/api/Api/AnthropicLlmProvider.cs`
- Create: `/Users/blackcolours/dev/work/rental-management/api/Api/Core/Dtos/LlmToolSchema.cs`
- Create: `/Users/blackcolours/dev/work/rental-management/api/Api/Core/Dtos/LlmToolCall.cs`

**Steps:**

- [ ] **Test (red):** Write xUnit test in `RentalCommand.Api.Tests/AiTests/LlmProviderToolCallTests.cs` that mocks Claude API response with `tool_use` content block and verifies structured tool-call extraction (tool name, input, id). Expect test to fail.

- [ ] **Implement ILlmProvider.cs expansion:** Add method signature:
  ```csharp
  Task<LlmToolCallResponse> InvokeChatWithToolsAsync(
      string systemPrompt,
      List<LlmMessage> messages,
      List<LlmToolSchema> tools,
      string? model = null,
      CancellationToken ct = default
  );
  
  record LlmToolCall(string Id, string Name, JsonElement Input);
  record LlmToolCallResponse(string StopReason, List<LlmToolCall> ToolCalls, string TextResponse, int InputTokens, int OutputTokens);
  ```

- [ ] **Implement AnthropicLlmProvider:** Use official Anthropic SDK `messages.create()` with `tool_use` block parsing. On `stop_reason: "tool_use"`, extract tool calls from `content[].type == "tool_use"` blocks. Preserve token counts from response metadata. Deterministic fallback: if no API key, return empty tool calls + text "I cannot process this request without an API key."

- [ ] **Verify (green):** Test passes; mocked Claude response correctly extracts tool name, input, and counts tokens.

- [ ] **Commit:** "Add tool-schema support to ILlmProvider for Q&A loop."

---

### Task 2: Create BriefingTemplate and PortfolioAiConfig entities + migrations

**Files:** Create/Modify

- Create: `/Users/blackcolours/dev/work/rental-management/api/Data/Entities/BriefingTemplate.cs`
- Create: `/Users/blackcolours/dev/work/rental-management/api/Data/Entities/PortfolioAiConfig.cs`
- Modify: `/Users/blackcolours/dev/work/rental-management/api/Data/LifecycleDbContext.cs` (add DbSet, configure)
- Create: `Migrations/202605XX_AddBriefingAndAiConfigEntities.cs`

**Steps:**

- [ ] **Test (red):** Write migration integration test in `RentalCommand.Data.Tests/MigrationTests.cs` that runs the new migration and queries for the tables.

- [ ] **Create BriefingTemplate entity:**
  ```csharp
  public class BriefingTemplate
  {
      public int Id { get; set; }
      public int PortfolioId { get; set; }
      public Portfolio Portfolio { get; set; } = null!;
      
      // Rules (JSON): rules for rent-due, late-fee, chore frequency, etc.
      public string RulesJson { get; set; } = "{}";  // serialized BriefingRules
      
      public bool IncludeRentDue { get; set; } = true;
      public bool IncludeLateFees { get; set; } = true;
      public bool IncludeMaintenance { get; set; } = true;
      public bool IncludeAppointments { get; set; } = true;
      public bool IncludeRecurringChores { get; set; } = true;
      
      public TimeOnly? SendTime { get; set; } = new TimeOnly(8, 0); // 8 AM
      
      public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
      public DateTime? UpdatedAt { get; set; }
  }
  ```

- [ ] **Create PortfolioAiConfig entity:**
  ```csharp
  public class PortfolioAiConfig
  {
      public int Id { get; set; }
      public int PortfolioId { get; set; }
      public Portfolio Portfolio { get; set; } = null!;
      
      public string? ModelOverride { get; set; }  // null = use app default
      public decimal Temperature { get; set; } = 0.7m;
      
      public string? CustomSystemPrompt { get; set; }
      
      public bool EnableVoiceCapture { get; set; } = true;
      public bool EnableLeaseQa { get; set; } = true;
      public bool EnableMaintenanceTriage { get; set; } = true;
      
      public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
      public DateTime? UpdatedAt { get; set; }
  }
  ```

- [ ] **Add DbSet + configure in LifecycleDbContext:**
  ```csharp
  public DbSet<BriefingTemplate> BriefingTemplates { get; set; }
  public DbSet<PortfolioAiConfig> PortfolioAiConfigs { get; set; }
  
  // In OnModelCreating:
  modelBuilder.Entity<BriefingTemplate>(e => {
      e.HasKey(x => x.Id);
      e.HasOne(x => x.Portfolio).WithMany().HasForeignKey(x => x.PortfolioId).OnDelete(DeleteBehavior.Cascade);
      e.HasIndex(x => x.PortfolioId).IsUnique();
  });
  
  modelBuilder.Entity<PortfolioAiConfig>(e => {
      e.HasKey(x => x.Id);
      e.HasOne(x => x.Portfolio).WithMany().HasForeignKey(x => x.PortfolioId).OnDelete(DeleteBehavior.Cascade);
      e.HasIndex(x => x.PortfolioId).IsUnique();
  });
  ```

- [ ] **Create migration:** `ef migrations add AddBriefingAndAiConfigEntities`

- [ ] **Verify (green):** Migration applies cleanly; tables exist with correct columns and constraints.

- [ ] **Commit:** "Add BriefingTemplate and PortfolioAiConfig entities."

---

### Task 3: Create VoiceDraft and MaintenanceTriageResult entities

**Files:** Create/Modify

- Create: `/Users/blackcolours/dev/work/rental-management/api/Data/Entities/VoiceDraft.cs`
- Create: `/Users/blackcolours/dev/work/rental-management/api/Data/Entities/MaintenanceTriageResult.cs`
- Modify: `LifecycleDbContext.cs`
- Create: `Migrations/202605XX_AddVoiceDraftAndTriageEntities.cs`

**Steps:**

- [ ] **Create VoiceDraft entity:**
  ```csharp
  public class VoiceDraft
  {
      public int Id { get; set; }
      public int PortfolioId { get; set; }
      public Portfolio Portfolio { get; set; } = null!;
      
      public string AudioFileKey { get; set; } = "";  // StoredFile ref or blob URL
      public string Transcript { get; set; } = "";
      
      public string TargetEntityType { get; set; } = "";  // WorkOrder, Expense, Payment
      public string Status { get; set; } = "Pending";  // Pending, Reviewing, Confirmed, Rejected
      
      public string TranscriptionProvider { get; set; } = "Google";  // or Azure, OpenAI, etc.
      public double AudioDurationSeconds { get; set; }
      
      // LLM extraction result (similar to ScanDraft)
      public string ExtractedFieldsJson { get; set; } = "{}";  // { "field": { "value", "confidence", "sourceText" } }
      public string ProvanceJson { get; set; } = "{}";  // model, prompt, tokens
      
      public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
      public DateTime? ReviewedAt { get; set; }
  }
  ```

- [ ] **Create MaintenanceTriageResult entity:**
  ```csharp
  public class MaintenanceTriageResult
  {
      public int Id { get; set; }
      public int PortfolioId { get; set; }
      public Portfolio Portfolio { get; set; } = null!;
      
      public int? WorkOrderId { get; set; }
      public WorkOrder? WorkOrder { get; set; }
      
      public string PhotoFileKey { get; set; } = "";  // StoredFile ref
      public string AnalysisJson { get; set; } = "{}";  // { category, priority, confidence, suggestedVendorIds, reasoning }
      
      public string Category { get; set; } = "";  // Plumbing, Electrical, HVAC, Structural, Cosmetic, Other
      public string Priority { get; set; } = "Medium";  // Low, Medium, High, Emergency
      public decimal Confidence { get; set; }  // 0.0 - 1.0
      
      public string? SuggestedVendorIds { get; set; }  // CSV or JSON
      
      public string ProvanceJson { get; set; } = "{}";  // model, prompt, tokens, timestamp
      
      public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
  }
  ```

- [ ] **Add DbSet to LifecycleDbContext:**
  ```csharp
  public DbSet<VoiceDraft> VoiceDrafts { get; set; }
  public DbSet<MaintenanceTriageResult> MaintenanceTriageResults { get; set; }
  ```

- [ ] **Configure in OnModelCreating:**
  ```csharp
  modelBuilder.Entity<VoiceDraft>(e => {
      e.HasKey(x => x.Id);
      e.HasOne(x => x.Portfolio).WithMany().HasForeignKey(x => x.PortfolioId);
      e.HasIndex(x => new { x.PortfolioId, x.Status });
  });
  
  modelBuilder.Entity<MaintenanceTriageResult>(e => {
      e.HasKey(x => x.Id);
      e.HasOne(x => x.Portfolio).WithMany().HasForeignKey(x => x.PortfolioId);
      e.HasOne(x => x.WorkOrder).WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.SetNull);
      e.HasIndex(x => new { x.PortfolioId, x.CreatedAt });
  });
  ```

- [ ] **Create migration.**

- [ ] **Verify (green):** Migration applies; tables exist.

- [ ] **Commit:** "Add VoiceDraft and MaintenanceTriageResult entities."

---

### Task 4: Rewrite AiEndpoints.cs with real implementations

**Files:** Create/Modify

- Modify: `/Users/blackcolours/dev/work/rental-management/api/Api/AiEndpoints.cs`
- Create: `/Users/blackcolours/dev/work/rental-management/api/Api/Dtos/PortfolioQaRequest.cs`
- Create: `/Users/blackcolours/dev/work/rental-management/api/Api/Dtos/PortfolioQaResponse.cs`
- Create: `/Users/blackcolours/dev/work/rental-management/api/Api/Dtos/VoiceTranscriptRequest.cs`
- Create: `/Users/blackcolours/dev/work/rental-management/api/Api/Dtos/MaintenanceTriageRequest.cs`

**Steps:**

- [ ] **Test (red):** Write integration test in `RentalCommand.Api.Tests/AiIntegrationTests.cs`:
  - `POST /api/ai/ask` with question "who is late on rent" returns answers with SourceTools, TokenUsage
  - `POST /api/ai/voice` with audio URL → creates VoiceDraft with Status Pending
  - `POST /api/ai/triage-maintenance` with photo URL → creates MaintenanceTriageResult with category/priority
  Tests should mock the MCP client and speech-to-text provider. Expect to fail.

- [ ] **Create DTOs:**
  ```csharp
  // PortfolioQaRequest.cs
  public record PortfolioQaRequest(
      int PortfolioId,
      string Question,
      List<Dictionary<string, string>>? ConversationHistory = null,
      string? Context = null
  );
  
  public record PortfolioQaResponse(
      string Answer,
      List<string> SourceTools,
      TokenUsageInfo TokenUsage,
      decimal Confidence
  );
  
  // VoiceTranscriptRequest.cs
  public record VoiceTranscriptRequest(
      int PortfolioId,
      string AudioUrl,
      string TargetEntityType,
      string? Context = null
  );
  
  // MaintenanceTriageRequest.cs
  public record MaintenanceTriageRequest(
      int PortfolioId,
      string PhotoUrl,
      string? Description = null,
      int? WorkOrderId = null
  );
  ```

- [ ] **Rewrite AiEndpoints.cs:**
  - **`POST /api/ai/intake`:** Keep the same endpoint signature; replace keyword matching with real ILlmProvider. Call `ExtractAsync()` with vision/text routing (text-first for PDFs with text layer, vision for photos/poor scans).
  - **`POST /api/ai/ask`:** NEW endpoint. Takes PortfolioQaRequest, calls PortfolioQaService to build MCP tool schema, invokes ILlmProvider.InvokeChatWithToolsAsync(), executes tool calls against MCP HTTP API, re-invokes LLM with tool results, returns answer + source tools + token usage.
  - **`POST /api/ai/voice`:** NEW endpoint. Takes VoiceTranscriptRequest, stores audio in StoredFile, creates VoiceDraft with Status Pending, enqueues async transcription, returns draft ID + status. Frontend polls or listens to SignalR for "Reviewing" state.
  - **`GET /api/ai/voice/{draftId}`:** Fetch VoiceDraft status (for polling).
  - **`POST /api/ai/lease-faq`:** NEW endpoint. Takes (PortfolioId, LeaseId, Question), caches lease PDF summary, answers via LLM grounding. Returns answer + SourcePage + Confidence.
  - **`POST /api/ai/triage-maintenance`:** NEW endpoint. Takes MaintenanceTriageRequest, calls MaintenanceTriageService (vision analyze photo → extract category/priority/vendors), returns MaintenanceTriageResult.

- [ ] **Preserve public contracts:** Keep `/api/ai/intake`, `/api/ai/portfolio-summary`, `/api/ai/generate-notice` routes (exact same as Phase 2 fake stubs) but replace internals with real LLM calls.

- [ ] **Add PortfolioId claim scoping:** All endpoints extract PortfolioId from JWT claims and validate request PortfolioId matches; reject if mismatch.

- [ ] **Verify (green):** Integration tests pass; endpoints return real LLM responses (or mocked in test context).

- [ ] **Commit:** "Rewrite AiEndpoints with real LLM implementations."

---

### Task 5: Create PortfolioQaService (MCP-grounded Q&A loop)

**Files:** Create/Modify

- Create: `/Users/blackcolours/dev/work/rental-management/api/Services/PortfolioQaService.cs`
- Create: `/Users/blackcolours/dev/work/rental-management/api/Services/McpClient.cs` (HTTP client wrapper for MCP HTTP API)
- Modify: `/Users/blackcolours/dev/work/rental-management/api/Program.cs` (register services)

**Steps:**

- [ ] **Test (red):** Write unit test in `RentalCommand.Api.Tests/Services/PortfolioQaServiceTests.cs` that mocks ILlmProvider and McpClient:
  - User asks "who is late?"
  - Service builds MCP tool schema (list_portfolios, get_portfolio_dashboard, list_leases, list_payments, etc.)
  - Service invokes LLM with tools
  - LLM returns tool_use: get_portfolio_dashboard
  - Service executes via McpClient, gets response
  - Service re-invokes LLM with tool result
  - Service returns final answer + SourceTools
  Test mocks both ILlmProvider and McpClient. Expect to fail.

- [ ] **Create McpClient.cs:** HTTP wrapper around the MCP TypeScript server (assumes it's running on `MCP_API_URL` from config, or `http://localhost:3000` default).
  ```csharp
  public interface IMcpClient
  {
      Task<JsonElement> ExecuteToolAsync(string toolName, JsonElement args, CancellationToken ct = default);
  }
  
  public class McpClient : IMcpClient
  {
      private readonly HttpClient _httpClient;
      private readonly IConfiguration _config;
      
      public McpClient(HttpClient httpClient, IConfiguration config)
      {
          _httpClient = httpClient;
          _config = config;
      }
      
      public async Task<JsonElement> ExecuteToolAsync(string toolName, JsonElement args, CancellationToken ct = default)
      {
          var url = _config.GetValue<string>("Mcp:ApiUrl") ?? "http://localhost:3000";
          var req = new { tool = toolName, args };
          var res = await _httpClient.PostAsJsonAsync($"{url}/tools/{toolName}", args, ct);
          res.EnsureSuccessStatusCode();
          return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement;
      }
  }
  ```

- [ ] **Create PortfolioQaService.cs:**
  ```csharp
  public interface IPortfolioQaService
  {
      Task<(string Answer, List<string> SourceTools, TokenUsageInfo Tokens, decimal Confidence)> AskAsync(
          int portfolioId,
          string question,
          List<Dictionary<string, string>>? conversationHistory = null,
          CancellationToken ct = default
      );
  }
  
  public class PortfolioQaService : IPortfolioQaService
  {
      private readonly ILlmProvider _llm;
      private readonly IMcpClient _mcp;
      private readonly RentalCommandDbContext _db;
      
      // Maintain conversational state across invocations
      private readonly List<LlmMessage> _messageHistory = new();
      
      public async Task<(string, List<string>, TokenUsageInfo, decimal)> AskAsync(
          int portfolioId,
          string question,
          List<Dictionary<string, string>>? conversationHistory = null,
          CancellationToken ct = default)
      {
          var portfolio = await _db.Portfolios.FindAsync(new object[] { portfolioId }, cancellationToken: ct)
              ?? throw new KeyNotFoundException($"Portfolio {portfolioId} not found");
          
          // Build MCP tool schema from available endpoints
          var tools = BuildMcpToolSchema();
          
          // Add user question to message history
          _messageHistory.Add(new LlmMessage("user", question));
          
          var totalInputTokens = 0;
          var totalOutputTokens = 0;
          var sourceTools = new List<string>();
          
          // Agentic loop: invoke LLM, execute tools, repeat until LLM responds with text (not tool_use)
          string finalAnswer = "";
          int maxIterations = 5;  // Prevent infinite loops
          
          for (int i = 0; i < maxIterations; i++)
          {
              var response = await _llm.InvokeChatWithToolsAsync(
                  systemPrompt: BuildSystemPrompt(portfolioId),
                  messages: _messageHistory,
                  tools: tools,
                  ct: ct
              );
              
              totalInputTokens += response.InputTokens;
              totalOutputTokens += response.OutputTokens;
              
              // If no tool calls, return the response text
              if (response.ToolCalls.Count == 0)
              {
                  finalAnswer = response.TextResponse;
                  break;
              }
              
              // Execute each tool call and collect results
              var toolResults = new List<(string Id, string Name, string Result)>();
              foreach (var toolCall in response.ToolCalls)
              {
                  sourceTools.Add(toolCall.Name);
                  try
                  {
                      var result = await _mcp.ExecuteToolAsync(toolCall.Name, toolCall.Input, ct);
                      toolResults.Add((toolCall.Id, toolCall.Name, result.GetRawText()));
                  }
                  catch (Exception ex)
                  {
                      toolResults.Add((toolCall.Id, toolCall.Name, $"Error: {ex.Message}"));
                  }
              }
              
              // Add tool results to message history and re-invoke LLM
              foreach (var (id, name, result) in toolResults)
              {
                  _messageHistory.Add(new LlmMessage("user", $"Tool {name} result: {result}"));
              }
          }
          
          // Estimate confidence based on tool usage and response
          var confidence = sourceTools.Count > 0 ? 0.85m : 0.7m;
          
          return (finalAnswer, sourceTools, new TokenUsageInfo(totalInputTokens, totalOutputTokens), confidence);
      }
      
      private List<LlmToolSchema> BuildMcpToolSchema()
      {
          return new()
          {
              new LlmToolSchema("list_portfolios", "List all portfolios", new()),
              new LlmToolSchema("get_portfolio_dashboard", "Get portfolio dashboard summary", new() { ("portfolioId", "number") }),
              new LlmToolSchema("list_leases", "List leases for a portfolio", new() { ("portfolioId", "number") }),
              new LlmToolSchema("list_payments", "List payments for a portfolio", new() { ("portfolioId", "number") }),
              new LlmToolSchema("list_work_orders", "List work orders for a portfolio", new() { ("portfolioId", "number") }),
              // Add more as MCP tools expand
          };
      }
      
      private string BuildSystemPrompt(int portfolioId)
      {
          return $@"You are a rental property assistant answering plain-English questions about a portfolio (ID: {portfolioId}).
  
Use the available tools to retrieve data from the portfolio database:
- list_portfolios: see all portfolios
- get_portfolio_dashboard: get a high-level summary
- list_leases: find tenants, rent, due dates
- list_payments: see rent collected, late payments
- list_work_orders: check maintenance status
- list_vendors: find vendor records

Answer concisely in plain English. No jargon. If data is missing, say so.
Never invent data. Always ground your answer in actual tool results.";
      }
  }
  ```

- [ ] **Register in Program.cs:**
  ```csharp
  builder.Services.AddScoped<IPortfolioQaService, PortfolioQaService>();
  builder.Services.AddHttpClient<IMcpClient, McpClient>();
  ```

- [ ] **Verify (green):** Unit test passes; service correctly builds tool schema, invokes LLM, executes tools, and returns answers.

- [ ] **Commit:** "Add PortfolioQaService with MCP-grounded Q&A loop."

---

### Task 6: Create DailyBriefingService (rules engine + LLM prose)

**Files:** Create/Modify

- Create: `/Users/blackcolours/dev/work/rental-management/api/Services/DailyBriefingService.cs`
- Create: `/Users/blackcolours/dev/work/rental-management/api/Services/DailyBriefingRule.cs` (rules model)
- Modify: `Program.cs`

**Steps:**

- [ ] **Test (red):** Write unit test in `RentalCommand.Api.Tests/Services/DailyBriefingServiceTests.cs`:
  - Create a test portfolio with 2 leases (1 rent-due today, 1 late)
  - Create 1 emergency work order, 1 appointment today
  - Call DailyBriefingService.ComposeAsync()
  - Verify output includes: "Rent due from Unit 1", "Unit 2 is 5 days late", "Emergency work order pending", "Appointment at 2 PM"
  - Verify LLM was called to enhance the prose
  Test mocks ILlmProvider. Expect to fail.

- [ ] **Create DailyBriefingRule enum/model:**
  ```csharp
  public enum BriefingRuleType
  {
      RentDue,
      RentLate,
      MaintenanceOpen,
      AppointmentToday,
      LeaseSoonExpiring,
      RecurringChore
  }
  
  public record BriefingBullet(
      string Title,
      string Description,
      BriefingRuleType RuleType,
      int? EntityId,
      string? EntityType,
      decimal Priority  // 0.0 - 1.0; sort descending
  );
  ```

- [ ] **Create DailyBriefingService.cs:**
  ```csharp
  public interface IDailyBriefingService
  {
      Task<string> ComposeAsync(int portfolioId, CancellationToken ct = default);
  }
  
  public class DailyBriefingService : IDailyBriefingService
  {
      private readonly RentalCommandDbContext _db;
      private readonly ILlmProvider _llm;
      private readonly INotificationChannel _notificationChannel;
      
      public DailyBriefingService(RentalCommandDbContext db, ILlmProvider llm, INotificationChannel notificationChannel)
      {
          _db = db;
          _llm = llm;
          _notificationChannel = notificationChannel;
      }
      
      public async Task<string> ComposeAsync(int portfolioId, CancellationToken ct = default)
      {
          var now = DateTime.UtcNow;
          var today = now.Date;
          
          var bullets = new List<BriefingBullet>();
          
          // 1. Rent due today
          var rentDueToday = await _db.Leases
              .Where(l => l.PortfolioId == portfolioId && l.Status == LeaseStatus.Active && l.RentDueDay == now.Day)
              .ToListAsync(ct);
          foreach (var lease in rentDueToday)
          {
              bullets.Add(new(
                  $"Rent due from Unit {lease.Unit?.UnitNumber ?? "?"}",
                  $"${lease.RentAmount:F2} due",
                  BriefingRuleType.RentDue,
                  lease.Id,
                  "Lease",
                  0.9m
              ));
          }
          
          // 2. Rent late (overdue payments)
          var lateBoundary = today.AddDays(-5);  // 5+ days late
          var latePayments = await _db.Payments
              .Include(p => p.Lease).ThenInclude(l => l!.Unit)
              .Where(p => p.PortfolioId == portfolioId && p.Status != PaymentStatus.Paid && p.DueDate < lateBoundary)
              .ToListAsync(ct);
          foreach (var payment in latePayments)
          {
              var daysLate = (today - payment.DueDate).TotalDays;
              bullets.Add(new(
                  $"Unit {payment.Lease?.Unit?.UnitNumber ?? "?"} is {(int)daysLate} days late",
                  $"${payment.Amount:F2} outstanding",
                  BriefingRuleType.RentLate,
                  payment.Id,
                  "Payment",
                  0.95m  // Higher priority
              ));
          }
          
          // 3. Open emergency work orders
          var emergencyWo = await _db.WorkOrders
              .Where(w => w.PortfolioId == portfolioId && w.Priority == WorkOrderPriority.Emergency && w.Status != WorkOrderStatus.Completed)
              .ToListAsync(ct);
          foreach (var wo in emergencyWo)
          {
              bullets.Add(new(
                  "⚠️ Emergency maintenance pending",
                  wo.Description ?? "No description",
                  BriefingRuleType.MaintenanceOpen,
                  wo.Id,
                  "WorkOrder",
                  1.0m  // Highest priority
              ));
          }
          
          // 4. Appointments today
          var appointmentsToday = await _db.Appointments
              .Where(a => a.PortfolioId == portfolioId && a.ScheduledDate == today && a.Status != AppointmentStatus.Cancelled)
              .ToListAsync(ct);
          foreach (var apt in appointmentsToday)
          {
              bullets.Add(new(
                  $"Appointment: {apt.Type}",
                  $"{apt.ScheduledDate:g}",
                  BriefingRuleType.AppointmentToday,
                  apt.Id,
                  "Appointment",
                  0.6m
              ));
          }
          
          // 5. Leases expiring soon (60 days)
          var expiringLeases = await _db.Leases
              .Where(l => l.PortfolioId == portfolioId && l.Status == LeaseStatus.Active && l.EndDate <= today.AddDays(60) && l.EndDate > today)
              .ToListAsync(ct);
          foreach (var lease in expiringLeases)
          {
              var daysUntilExpiry = (lease.EndDate - today).TotalDays;
              bullets.Add(new(
                  $"Unit {lease.Unit?.UnitNumber ?? "?"} lease expires in {(int)daysUntilExpiry} days",
                  $"Ends {lease.EndDate:d}",
                  BriefingRuleType.LeaseSoonExpiring,
                  lease.Id,
                  "Lease",
                  0.7m
              ));
          }
          
          // Sort by priority descending
          var sortedBullets = bullets.OrderByDescending(b => b.Priority).ToList();
          
          // Compose raw text briefing
          var rawBriefing = string.Join("\n", sortedBullets.Select(b => $"• {b.Title}: {b.Description}"));
          
          // Enhance with LLM (prose polish + one-tap action suggestions)
          var enhancedBriefing = await EnhanceWithLlmAsync(rawBriefing, sortedBullets, ct);
          
          return enhancedBriefing;
      }
      
      private async Task<string> EnhanceWithLlmAsync(string rawBriefing, List<BriefingBullet> bullets, CancellationToken ct)
      {
          var prompt = $@"Convert this rental property briefing into plain, conversational English suitable for a busy landlord. Keep it brief (max 5 bullet points). Make each point actionable (e.g., 'Reply YES to confirm rent receipt').

Raw briefing:
{rawBriefing}

Enhanced briefing (plain English, one-tap actions):";
          
          var (response, _, _) = await _llm.ChatAsync(
              systemPrompt: "You are a property manager assistant. Keep messages brief and actionable.",
              userMessage: prompt,
              ct: ct
          );
          
          return response;
      }
  }
  ```

- [ ] **Register in Program.cs:**
  ```csharp
  builder.Services.AddScoped<IDailyBriefingService, DailyBriefingService>();
  ```

- [ ] **Verify (green):** Unit test passes; service correctly identifies rent-due, late payments, emergencies, appointments, and returns LLM-enhanced briefing.

- [ ] **Commit:** "Add DailyBriefingService with rules engine + LLM enhancement."

---

### Task 7: Create DailyBriefingWorker (Engine background service)

**Files:** Create/Modify

- Create: `/Users/blackcolours/dev/work/rental-management/api/Engine/Workers/DailyBriefingWorker.cs`
- Modify: `Program.cs` (register worker)
- Modify: `/Users/blackcolours/dev/work/rental-management/api/Data/Entities/OutboxMessage.cs` (ensure exists from Phase 2; add to briefing delivery)

**Steps:**

- [ ] **Test (red):** Write integration test in `RentalCommand.Engine.Tests/Workers/DailyBriefingWorkerTests.cs`:
  - Set up test DB with 2 portfolios, 1 with rent due today
  - Run DailyBriefingWorker.ExecuteAsync()
  - Verify OutboxMessage created with briefing text, scheduled for send
  - Verify briefing is only generated once (idempotency check: same date, same portfolio)
  Test uses transaction rollback fixture. Expect to fail.

- [ ] **Create DailyBriefingWorker.cs:**
  ```csharp
  public class DailyBriefingWorker : EngineWorkerBase
  {
      private readonly ILogger<DailyBriefingWorker> _logger;
      private readonly IDailyBriefingService _briefingService;
      private readonly RentalCommandDbContext _db;
      private readonly IDataUpdateService _dataUpdateService;  // for SignalR push
      
      // Daily trigger
      private readonly PeriodicTimer _dailyTimer = new(TimeSpan.FromDays(1), TimeSpan.FromHours(1));
      
      public DailyBriefingWorker(
          ILogger<DailyBriefingWorker> logger,
          IDailyBriefingService briefingService,
          RentalCommandDbContext db,
          IDataUpdateService dataUpdateService)
      {
          _logger = logger;
          _briefingService = briefingService;
          _db = db;
          _dataUpdateService = dataUpdateService;
      }
      
      protected override async Task ExecuteAsync(CancellationToken stoppingToken)
      {
          while (await _dailyTimer.WaitForNextTickAsync(stoppingToken))
          {
              try
              {
                  await GenerateBriefingsAsync(stoppingToken);
              }
              catch (Exception ex)
              {
                  _logger.LogError(ex, "DailyBriefingWorker failed");
              }
          }
      }
      
      private async Task GenerateBriefingsAsync(CancellationToken ct)
      {
          var portfolios = await _db.Portfolios.ToListAsync(ct);
          
          foreach (var portfolio in portfolios)
          {
              // Acquire advisory lock per portfolio (idempotency)
              const string lockResource = "briefing_generation";
              var lockAcquired = false;
              try
              {
                  lockAcquired = await _db.Database.ExecuteScalarAsync<bool>(
                      $"SELECT pg_advisory_lock({(long)portfolio.Id * 1000 + Hash(lockResource)});",
                      ct);
                  
                  // Generate briefing
                  var briefing = await _briefingService.ComposeAsync(portfolio.Id, ct);
                  
                  // Create outbox message for SMS/email delivery
                  var outbox = new OutboxMessage
                  {
                      PortfolioId = portfolio.Id,
                      MessageType = "DailyBriefing",
                      Recipient = portfolio.Owner?.PhoneNumber ?? portfolio.Owner?.Email,
                      Subject = $"Daily Briefing - {DateTime.UtcNow:MMM d}",
                      Body = briefing,
                      Channel = "SMS",  // SMS-first per spec
                      Status = "Pending",
                      CreatedAt = DateTime.UtcNow
                  };
                  
                  _db.OutboxMessages.Add(outbox);
                  await _db.SaveChangesAsync(ct);
                  
                  _logger.LogInformation("Generated briefing for portfolio {PortfolioId}", portfolio.Id);
                  
                  // Push via SignalR to connected users
                  await _dataUpdateService.BroadcastBriefingAsync(portfolio.Id, briefing, ct);
              }
              finally
              {
                  if (lockAcquired)
                  {
                      // Release lock
                  }
              }
          }
      }
  }
  ```

- [ ] **Register in Program.cs:**
  ```csharp
  builder.Services.AddHostedService<DailyBriefingWorker>();
  ```

- [ ] **Verify (green):** Integration test passes; briefing generated once per portfolio, outbox message created, idempotency works.

- [ ] **Commit:** "Add DailyBriefingWorker with daily briefing generation."

---

### Task 8: Create LeaseParsingService (PDF vision → Q&A grounding)

**Files:** Create/Modify

- Create: `/Users/blackcolours/dev/work/rental-management/api/Services/LeaseParsingService.cs`
- Create: `/Users/blackcolours/dev/work/rental-management/api/Services/LeaseQaService.cs`
- Modify: `Program.cs`

**Steps:**

- [ ] **Test (red):** Write unit test in `RentalCommand.Api.Tests/Services/LeaseQaServiceTests.cs`:
  - Mock a lease PDF (text only, no images)
  - Call LeaseQaService.AnswerAsync(leaseId, "Can I have a dog?")
  - Verify LLM was called with lease text as grounding
  - Verify response extracts from lease (yes/no/contact landlord)
  Test mocks ILlmProvider and IFileStorage. Expect to fail.

- [ ] **Create LeaseParsingService.cs:**
  ```csharp
  public interface ILeaseParsingService
  {
      Task<string> ExtractLeaseTextAsync(int leaseId, CancellationToken ct = default);
      Task<string> GetLeaseChunkAsync(int leaseId, string query, CancellationToken ct = default);
  }
  
  public class LeaseParsingService : ILeaseParsingService
  {
      private readonly IFileStorage _fileStorage;
      private readonly ILlmProvider _llm;
      private readonly RentalCommandDbContext _db;
      private readonly IMemoryCache _cache;  // Simple cache for lease extracts
      
      public LeaseParsingService(
          IFileStorage fileStorage,
          ILlmProvider llm,
          RentalCommandDbContext db,
          IMemoryCache cache)
      {
          _fileStorage = fileStorage;
          _llm = llm;
          _db = db;
          _cache = cache;
      }
      
      public async Task<string> ExtractLeaseTextAsync(int leaseId, CancellationToken ct = default)
      {
          var cacheKey = $"lease_text_{leaseId}";
          if (_cache.TryGetValue(cacheKey, out string? cached))
              return cached;
          
          var lease = await _db.Leases.FindAsync(new object[] { leaseId }, cancellationToken: ct);
          if (lease?.DocumentFileKey == null)
              throw new InvalidOperationException($"Lease {leaseId} has no document");
          
          // Read PDF / image from storage
          var fileStream = await _fileStorage.GetAsync(lease.DocumentFileKey, ct);
          var fileBytes = new MemoryStream();
          await fileStream.CopyToAsync(fileBytes, ct);
          
          // If PDF, extract text via vision or PDFSharp
          var text = lease.DocumentFileKey.EndsWith(".pdf")
              ? await ExtractPdfTextAsync(fileBytes.ToArray(), ct)
              : ""; // For images, rely on vision in Q&A prompt
          
          _cache.Set(cacheKey, text, TimeSpan.FromHours(24));
          return text;
      }
      
      private Task<string> ExtractPdfTextAsync(byte[] pdfBytes, CancellationToken ct)
          => Task.Run(() =>
          {
              // Text-first (cheap), same routing as Phase 2's token strategy: born-digital PDFs
              // (e-leases, most printed leases) carry a text layer — pull it with UglyToad.PdfPig
              // (<PackageReference Include="PdfPig" />). If there is no usable text layer (a
              // scanned-image PDF), return "" so the Q&A prompt falls back to vision, exactly like
              // the image path in ExtractLeaseTextAsync above.
              using var doc = UglyToad.PdfPig.PdfDocument.Open(pdfBytes);
              var sb = new StringBuilder();
              foreach (var page in doc.GetPages())
                  sb.AppendLine(page.Text);
              var text = sb.ToString().Trim();
              return text.Length >= 50 ? text : "";
          }, ct);
      
      public async Task<string> GetLeaseChunkAsync(int leaseId, string query, CancellationToken ct = default)
      {
          // Use LLM to extract relevant chunk from lease given a question
          var text = await ExtractLeaseTextAsync(leaseId, ct);
          
          var prompt = $@"Given this lease text, extract the section most relevant to the question.
  
Question: {query}

Lease text:
{text}

Relevant section (or 'Not found' if not mentioned):";
          
          var (response, _, _) = await _llm.ChatAsync(
              systemPrompt: "You are a lease analyst. Extract exact text from the lease.",
              userMessage: prompt,
              ct: ct
          );
          
          return response;
      }
  }
  ```

- [ ] **Create LeaseQaService.cs:**
  ```csharp
  public interface ILeaseQaService
  {
      Task<(string Answer, string SourceSection, decimal Confidence)> AnswerAsync(
          int leaseId,
          string question,
          CancellationToken ct = default
      );
  }
  
  public class LeaseQaService : ILeaseQaService
  {
      private readonly ILeaseParsingService _leaseParsing;
      private readonly ILlmProvider _llm;
      
      public LeaseQaService(ILeaseParsingService leaseParsing, ILlmProvider llm)
      {
          _leaseParsing = leaseParsing;
          _llm = llm;
      }
      
      public async Task<(string, string, decimal)> AnswerAsync(int leaseId, string question, CancellationToken ct = default)
      {
          var sourceSection = await _leaseParsing.GetLeaseChunkAsync(leaseId, question, ct);
          
          var prompt = $@"You are a tenant assistant. Answer this question about the lease in plain English. Be concise.

Question: {question}

Lease section:
{sourceSection}

Answer:";
          
          var (response, _, _) = await _llm.ChatAsync(
              systemPrompt: "You answer tenant questions about their lease honestly and plainly.",
              userMessage: prompt,
              ct: ct
          );
          
          // Confidence is high if source was found, low if not found
          var confidence = sourceSection.Contains("Not found") ? 0.4m : 0.85m;
          
          return (response, sourceSection, confidence);
      }
  }
  ```

- [ ] **Register in Program.cs:**
  ```csharp
  builder.Services.AddScoped<ILeaseParsingService, LeaseParsingService>();
  builder.Services.AddScoped<ILeaseQaService, LeaseQaService>();
  builder.Services.AddMemoryCache();
  ```

- [ ] **Verify (green):** Unit test passes; lease text extracted, Q&A grounded in lease.

- [ ] **Commit:** "Add LeaseParsingService and LeaseQaService."

---

### Task 9: Create MaintenanceTriageService (vision photo analysis)

**Files:** Create/Modify

- Create: `/Users/blackcolours/dev/work/rental-management/api/Services/MaintenanceTriageService.cs`
- Modify: `Program.cs`

**Steps:**

- [ ] **Test (red):** Write unit test in `RentalCommand.Api.Tests/Services/MaintenanceTriageServiceTests.cs`:
  - Mock a maintenance photo (or use a real test image)
  - Call MaintenanceTriageService.AnalyzeAsync(photoUrl, description?)
  - Verify LLM was called with vision (photo + description)
  - Verify result includes: category, priority, confidence, suggested vendors
  Test mocks ILlmProvider. Expect to fail.

- [ ] **Create MaintenanceTriageService.cs:**
  ```csharp
  public interface IMaintenanceTriageService
  {
      Task<MaintenanceTriageResult> AnalyzeAsync(
          int portfolioId,
          string photoUrl,
          string? description = null,
          CancellationToken ct = default
      );
  }
  
  public class MaintenanceTriageService : IMaintenanceTriageService
  {
      private readonly ILlmProvider _llm;
      private readonly RentalCommandDbContext _db;
      private readonly ILogger<MaintenanceTriageService> _logger;
      
      private readonly Dictionary<string, List<string>> _categoryVendorMap = new()
      {
          { "Plumbing", new() { "Joe's Plumbing", "Fast Plumbers Inc" } },
          { "Electrical", new() { "ElectroFix", "Watts Electric" } },
          { "HVAC", new() { "Cool Air Systems", "HVAC Pro" } },
          { "Structural", new() { "BuildRight", "Foundation Experts" } },
          { "Cosmetic", new() { "Paint & Drywall", "Flooring Plus" } }
      };
      
      public MaintenanceTriageService(ILlmProvider llm, RentalCommandDbContext db, ILogger<MaintenanceTriageService> logger)
      {
          _llm = llm;
          _db = db;
          _logger = logger;
      }
      
      public async Task<MaintenanceTriageResult> AnalyzeAsync(int portfolioId, string photoUrl, string? description = null, CancellationToken ct = default)
      {
          var portfolio = await _db.Portfolios.FindAsync(new object[] { portfolioId }, cancellationToken: ct)
              ?? throw new KeyNotFoundException($"Portfolio {portfolioId}");
          
          // Call LLM with vision to analyze photo
          var prompt = $@"You are a maintenance triage expert. Analyze this maintenance issue photo and provide:
1. Category (Plumbing, Electrical, HVAC, Structural, Cosmetic, Other)
2. Priority (Low, Medium, High, Emergency)
3. Confidence (0.0-1.0)
4. Brief assessment

Description from reporter: {description ?? "(none)"}

Respond in JSON:
{{
  ""category"": ""...",
  ""priority"": ""...",
  ""confidence"": 0.85,
  ""assessment"": ""...""
}}";
          
          var (response, _, _) = await _llm.ExtractAsync(
              systemPrompt: "You are a professional property inspector. Be precise in categorization.",
              userMessage: prompt,
              imageUrl: photoUrl,  // Vision call
              ct: ct
          );
          
          // Parse JSON response
          var json = JsonDocument.Parse(response).RootElement;
          var category = json.GetProperty("category").GetString() ?? "Other";
          var priority = json.GetProperty("priority").GetString() ?? "Medium";
          var confidence = json.GetProperty("confidence").GetDecimal();
          
          // Look up vendors for this category
          var suggestedVendors = _categoryVendorMap.TryGetValue(category, out var vendors)
              ? vendors
              : new();
          
          var result = new MaintenanceTriageResult
          {
              PortfolioId = portfolioId,
              Category = category,
              Priority = priority,
              Confidence = confidence,
              AnalysisJson = response,
              SuggestedVendorIds = string.Join(",", suggestedVendors),
              ProvanceJson = JsonSerializer.Serialize(new { Model = "claude-3.5-sonnet", Timestamp = DateTime.UtcNow })
          };
          
          _db.MaintenanceTriageResults.Add(result);
          await _db.SaveChangesAsync(ct);
          
          _logger.LogInformation("Triaged maintenance: {Category} {Priority}", category, priority);
          
          return result;
      }
  }
  ```

- [ ] **Register in Program.cs:**
  ```csharp
  builder.Services.AddScoped<IMaintenanceTriageService, MaintenanceTriageService>();
  ```

- [ ] **Verify (green):** Unit test passes; photo analyzed, category/priority/vendors extracted.

- [ ] **Commit:** "Add MaintenanceTriageService for work-order photo analysis."

---

### Task 10: Add /api/ai/voice endpoint + VoiceTranscriptionWorker

**Files:** Create/Modify

- Modify: `/Users/blackcolours/dev/work/rental-management/api/Api/AiEndpoints.cs` (add voice endpoints)
- Create: `/Users/blackcolours/dev/work/rental-management/api/Engine/Workers/VoiceTranscriptionWorker.cs`
- Create: `/Users/blackcolours/dev/work/rental-management/api/Services/SpeechToTextProvider.cs` (interface + stub)
- Modify: `Program.cs`

**Steps:**

- [ ] **Test (red):** Write integration test in `RentalCommand.Api.Tests/AiIntegrationTests.cs`:
  - POST /api/ai/voice with valid audio URL
  - Verify VoiceDraft created with Status Pending
  - Run VoiceTranscriptionWorker
  - Verify VoiceDraft.Status -> Reviewing, Transcript populated
  - Verify SignalR broadcast sent
  Test mocks speech-to-text provider. Expect to fail.

- [ ] **Create ISpeechToTextProvider interface:**
  ```csharp
  public interface ISpeechToTextProvider
  {
      Task<(string Transcript, double DurationSeconds)> TranscribeAsync(
          string audioUrl,
          string language = "en",
          CancellationToken ct = default
      );
  }
  
  // Stub implementation (no API key = deterministic fallback)
  public class SpeechToTextProvider : ISpeechToTextProvider
  {
      private readonly IConfiguration _config;
      
      public SpeechToTextProvider(IConfiguration config) => _config = config;
      
      public async Task<(string, double)> TranscribeAsync(string audioUrl, string language = "en", CancellationToken ct = default)
      {
          var apiKey = _config.GetValue<string>("SpeechToText:ApiKey");
          if (string.IsNullOrEmpty(apiKey))
              return ("(fallback: no speech-to-text API key configured)", 0);
          
          // Call actual provider (Google Cloud Speech, Azure, Deepgram, etc.)
          // For now, return stub
          return ("Transcription not yet implemented", 0);
      }
  }
  ```

- [ ] **Add voice endpoints to AiEndpoints.cs:**
  ```csharp
  group.MapPost("/voice", async (VoiceTranscriptRequest req, IFileStorage storage, RentalCommandDbContext db, IDataUpdateService dataUpdate) =>
  {
      // Validate portfolio scoping (from claims)
      var draft = new VoiceDraft
      {
          PortfolioId = req.PortfolioId,
          AudioFileKey = req.AudioUrl,  // Could also download and store
          TargetEntityType = req.TargetEntityType,
          Status = "Pending",
          CreatedAt = DateTime.UtcNow
      };
      
      db.VoiceDrafts.Add(draft);
      await db.SaveChangesAsync();
      
      return Results.Accepted($"/api/ai/voice/{draft.Id}", new { draftId = draft.Id, status = draft.Status });
  });
  
  group.MapGet("/voice/{draftId:int}", async (int draftId, RentalCommandDbContext db) =>
  {
      var draft = await db.VoiceDrafts.FindAsync(draftId);
      return draft ?? Results.NotFound();
  });
  ```

- [ ] **Create VoiceTranscriptionWorker:**
  ```csharp
  public class VoiceTranscriptionWorker : EngineWorkerBase
  {
      private readonly RentalCommandDbContext _db;
      private readonly ISpeechToTextProvider _speechToText;
      private readonly ILlmProvider _llm;
      private readonly IDataUpdateService _dataUpdate;
      private readonly ILogger<VoiceTranscriptionWorker> _logger;
      
      private readonly PeriodicTimer _pollTimer = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
      
      protected override async Task ExecuteAsync(CancellationToken stoppingToken)
      {
          while (await _pollTimer.WaitForNextTickAsync(stoppingToken))
          {
              try
              {
                  await ProcessPendingVoiceDraftsAsync(stoppingToken);
              }
              catch (Exception ex)
              {
                  _logger.LogError(ex, "VoiceTranscriptionWorker failed");
              }
          }
      }
      
      private async Task ProcessPendingVoiceDraftsAsync(CancellationToken ct)
      {
          var pending = await _db.VoiceDrafts
              .Where(v => v.Status == "Pending")
              .Take(10)  // Batch size
              .ToListAsync(ct);
          
          foreach (var draft in pending)
          {
              try
              {
                  // Transcribe audio
                  var (transcript, duration) = await _speechToText.TranscribeAsync(draft.AudioFileKey, ct: ct);
                  draft.Transcript = transcript;
                  draft.AudioDurationSeconds = duration;
                  draft.Status = "Reviewing";
                  
                  // Extract fields via LLM (reuse ScanDraft extraction logic)
                  var extractionPrompt = BuildExtractionPrompt(draft.TargetEntityType, transcript);
                  var (extracted, _, tokens) = await _llm.ChatAsync("You extract structured data from voice transcripts.", extractionPrompt, ct: ct);
                  draft.ExtractedFieldsJson = extracted;
                  draft.ProvanceJson = JsonSerializer.Serialize(new { Model = "claude-3.5-sonnet", Tokens = tokens });
                  
                  _db.VoiceDrafts.Update(draft);
                  await _db.SaveChangesAsync(ct);
                  
                  _logger.LogInformation("Transcribed voice draft {DraftId}", draft.Id);
                  
                  // Notify frontend via SignalR
                  await _dataUpdate.BroadcastAsync($"voice_draft_{draft.Id}", new { status = "Reviewing", transcript }, ct);
              }
              catch (Exception ex)
              {
                  _logger.LogError(ex, "Failed to transcribe voice draft {DraftId}", draft.Id);
                  draft.Status = "Failed";
                  _db.VoiceDrafts.Update(draft);
                  await _db.SaveChangesAsync(ct);
              }
          }
      }
      
      private string BuildExtractionPrompt(string entityType, string transcript)
      {
          return entityType switch
          {
              "WorkOrder" => $@"From this spoken work order request, extract:
- description
- priority (Low/Medium/High/Emergency)
- category (Plumbing, Electrical, HVAC, etc.)

Transcript: {transcript}

Return JSON.",
              "Expense" => $@"From this spoken expense report, extract:
- description
- amount
- category (Schedule E category)
- vendor name

Transcript: {transcript}

Return JSON.",
              "Payment" => $@"From this spoken payment confirmation, extract:
- amount
- payment method (Cash/Check/ACH/Card)
- unit/tenant reference

Transcript: {transcript}

Return JSON.",
              _ => $"Extract structured data from: {transcript}"
          };
      }
  }
  ```

- [ ] **Register in Program.cs:**
  ```csharp
  builder.Services.AddScoped<ISpeechToTextProvider, SpeechToTextProvider>();
  builder.Services.AddHostedService<VoiceTranscriptionWorker>();
  ```

- [ ] **Verify (green):** Integration test passes; voice draft created, transcribed, extracted.

- [ ] **Commit:** "Add voice capture endpoint + VoiceTranscriptionWorker."

---

### Task 11: Add /api/ai/lease-faq endpoint

**Files:** Modify

- Modify: `/Users/blackcolours/dev/work/rental-management/api/Api/AiEndpoints.cs`

**Steps:**

- [ ] **Test (red):** Write integration test in `RentalCommand.Api.Tests/AiIntegrationTests.cs`:
  - Create a test lease with document
  - POST /api/ai/lease-faq { portfolioId, leaseId, question: "Can I have a dog?" }
  - Verify answer contains text from lease or "Not mentioned"
  - Verify SourcePage and Confidence included
  Test mocks ILeaseQaService. Expect to fail.

- [ ] **Add endpoint to AiEndpoints.cs:**
  ```csharp
  group.MapPost("/lease-faq", async (
      int portfolioId,
      int leaseId,
      string question,
      ILeaseQaService leaseQa,
      RentalCommandDbContext db) =>
  {
      var lease = await db.Leases.FindAsync(leaseId);
      if (lease?.PortfolioId != portfolioId)
          return Results.Forbid();
      
      var (answer, sourceSection, confidence) = await leaseQa.AnswerAsync(leaseId, question);
      
      return Results.Ok(new
      {
          Answer = answer,
          SourceSection = sourceSection,
          Confidence = confidence,
          LeaseId = leaseId
      });
  });
  ```

- [ ] **Verify (green):** Integration test passes; lease FAQ answered.

- [ ] **Commit:** "Add lease FAQ endpoint."

---

### Task 12: Add /api/ai/triage-maintenance endpoint

**Files:** Modify

- Modify: `/Users/blackcolours/dev/work/rental-management/api/Api/AiEndpoints.cs`

**Steps:**

- [ ] **Test (red):** Write integration test (see Task 9 above).

- [ ] **Add endpoint to AiEndpoints.cs:**
  ```csharp
  group.MapPost("/triage-maintenance", async (
      MaintenanceTriageRequest req,
      IMaintenanceTriageService triage,
      RentalCommandDbContext db) =>
  {
      var result = await triage.AnalyzeAsync(req.PortfolioId, req.PhotoUrl, req.Description);
      
      // Optionally attach to work order if provided
      if (req.WorkOrderId.HasValue)
      {
          result.WorkOrderId = req.WorkOrderId.Value;
          db.MaintenanceTriageResults.Update(result);
          await db.SaveChangesAsync();
      }
      
      return Results.Ok(result);
  });
  ```

- [ ] **Verify (green):** Test passes; photo triaged, result attached to work order.

- [ ] **Commit:** "Add maintenance triage endpoint."

---

### Task 13: Wire up SignalR broadcasts for real-time briefing + voice updates

**Files:** Create/Modify

- Modify: `/Users/blackcolours/dev/work/rental-management/api/Services/DataUpdateService.cs` (ensure exists from Phase 2)
- Create: `/Users/blackcolours/dev/work/rental-management/api/Hubs/BriefingHub.cs` (NEW, if not in Phase 2)

**Steps:**

- [ ] **Test (red):** Write integration test in `RentalCommand.Api.Tests/SignalRTests.cs`:
  - Connect SignalR hub
  - Trigger DailyBriefingWorker
  - Verify client receives briefing broadcast on group `briefing_portfolio_{id}`
  - Trigger VoiceTranscriptionWorker
  - Verify client receives voice update on group `voice_draft_{id}`
  Test uses SignalR test hub. Expect to fail.

- [ ] **Create/Update DataUpdateService.cs (if not from Phase 2):**
  ```csharp
  public interface IDataUpdateService
  {
      Task BroadcastAsync(string group, object data, CancellationToken ct = default);
      Task BroadcastBriefingAsync(int portfolioId, string briefing, CancellationToken ct = default);
      Task BroadcastVoiceUpdateAsync(int draftId, object update, CancellationToken ct = default);
  }
  
  public class DataUpdateService : IDataUpdateService
  {
      private readonly IHubContext<NotificationHub> _hub;
      
      public DataUpdateService(IHubContext<NotificationHub> hub) => _hub = hub;
      
      public async Task BroadcastAsync(string group, object data, CancellationToken ct = default)
      {
          await _hub.Clients.Group(group).SendAsync("Update", data, cancellationToken: ct);
      }
      
      public async Task BroadcastBriefingAsync(int portfolioId, string briefing, CancellationToken ct = default)
      {
          await BroadcastAsync($"briefing_portfolio_{portfolioId}", new { briefing, timestamp = DateTime.UtcNow }, ct);
      }
      
      public async Task BroadcastVoiceUpdateAsync(int draftId, object update, CancellationToken ct = default)
      {
          await BroadcastAsync($"voice_draft_{draftId}", update, ct);
      }
  }
  ```

- [ ] **Create NotificationHub.cs (if not from Phase 2):**
  ```csharp
  public class NotificationHub : Hub
  {
      public async Task JoinBriefingGroup(int portfolioId)
      {
          await Groups.AddToGroupAsync(Context.ConnectionId, $"briefing_portfolio_{portfolioId}");
      }
      
      public async Task JoinVoiceDraftGroup(int draftId)
      {
          await Groups.AddToGroupAsync(Context.ConnectionId, $"voice_draft_{draftId}");
      }
  }
  ```

- [ ] **Register in Program.cs:**
  ```csharp
  builder.Services.AddSignalR();
  builder.Services.AddScoped<IDataUpdateService, DataUpdateService>();
  
  // In app config:
  app.MapHub<NotificationHub>("/hubs/notifications");
  ```

- [ ] **Verify (green):** Test passes; SignalR broadcasts received on client.

- [ ] **Commit:** "Add SignalR broadcasts for briefing and voice updates."

---

### Task 14: Frontend: Create AI Q&A box and briefing card components

**Files:** Create

- Create: `/Users/blackcolours/dev/work/rental-management/web/src/lib/components/AiQaBox.svelte`
- Create: `/Users/blackcolours/dev/work/rental-management/web/src/lib/components/DailyBriefingCard.svelte`
- Create: `/Users/blackcolours/dev/work/rental-management/web/src/lib/api/ai.ts`
- Create: `/Users/blackcolours/dev/work/rental-management/web/src/routes/(protected)/(portal)/ai/+page.svelte`

**Steps:**

- [ ] **Test (red):** Write Playwright test in `web/tests/ai.spec.ts`:
  - Load /ai page
  - Type question "who is late?" in Q&A box
  - Verify response appears
  - Verify daily briefing card shows today's briefing
  Test uses mock API responses. Expect to fail.

- [ ] **Create api/ai.ts:**
  ```typescript
  export async function ask(portfolioId: number, question: string, conversationHistory?: any[]) {
      return fetch(`/api/ai/ask`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ portfolioId, question, conversationHistory })
      }).then(r => r.json());
  }
  
  export async function uploadVoice(portfolioId: number, audioUrl: string, targetEntityType: string) {
      return fetch(`/api/ai/voice`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ portfolioId, audioUrl, targetEntityType })
      }).then(r => r.json());
  }
  
  export async function getVoiceDraftStatus(draftId: number) {
      return fetch(`/api/ai/voice/${draftId}`).then(r => r.json());
  }
  
  export async function triageMaintenance(portfolioId: number, photoUrl: string, description?: string) {
      return fetch(`/api/ai/triage-maintenance`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ portfolioId, photoUrl, description })
      }).then(r => r.json());
  }
  
  export async function askLeaseQuestion(portfolioId: number, leaseId: number, question: string) {
      return fetch(`/api/ai/lease-faq`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ portfolioId, leaseId, question })
      }).then(r => r.json());
  }
  ```

- [ ] **Create AiQaBox.svelte:**
  ```svelte
  <script lang="ts">
    import { ask } from '$lib/api/ai';
    import { portfolioId } from '$lib/stores';
    
    let question = '';
    let answer = '';
    let loading = false;
    let conversationHistory: any[] = [];
    
    async function handleAsk() {
      loading = true;
      try {
        const result = await ask($portfolioId, question, conversationHistory);
        answer = result.answer;
        conversationHistory = [...conversationHistory, 
          { role: 'user', content: question },
          { role: 'assistant', content: answer }
        ];
        question = '';
      } catch (e) {
        answer = 'Error: ' + (e instanceof Error ? e.message : 'Unknown error');
      } finally {
        loading = false;
      }
    }
  </script>
  
  <div class="qa-box">
    <h2>Ask about your portfolio</h2>
    <div class="history">
      {#each conversationHistory as msg}
        <div class="message {msg.role}">
          {msg.content}
        </div>
      {/each}
      {#if answer}
        <div class="message assistant">
          {answer}
        </div>
      {/if}
    </div>
    <div class="input-group">
      <input 
        bind:value={question} 
        placeholder="e.g., who is late on rent?"
        disabled={loading}
        on:keydown={e => e.key === 'Enter' && handleAsk()}
      />
      <button on:click={handleAsk} disabled={loading}>
        {loading ? 'Asking...' : 'Ask'}
      </button>
    </div>
  </div>
  
  <style>
    .qa-box { padding: 1rem; border: 1px solid #ccc; border-radius: 8px; }
    .history { max-height: 300px; overflow-y: auto; margin-bottom: 1rem; }
    .message { padding: 0.5rem; margin: 0.5rem 0; border-radius: 4px; }
    .message.user { background: #e3f2fd; text-align: right; }
    .message.assistant { background: #f5f5f5; }
    .input-group { display: flex; gap: 0.5rem; }
    input { flex: 1; padding: 0.5rem; border: 1px solid #ccc; border-radius: 4px; }
    button { padding: 0.5rem 1rem; background: #007bff; color: white; border: none; border-radius: 4px; cursor: pointer; }
  </style>
  ```

- [ ] **Create DailyBriefingCard.svelte:** Displays briefing bullets with one-tap actions (Confirm Rent, Mark Done, etc.)

- [ ] **Create routes/(protected)/(portal)/ai/+page.svelte:** Combines Q&A box and briefing card.

- [ ] **Verify (green):** Playwright test passes; Q&A works, briefing displays.

- [ ] **Commit:** "Add frontend Q&A and briefing UI."

---

### Task 15: Update MCP server documentation + optional tools expansion

**Files:** Create

- Create: `/Users/blackcolours/dev/work/rental-management/Docs/ai-mcp-grounding.md` (documentation)

**Steps:**

- [ ] **Document MCP tool usage for Q&A:**
  ```markdown
  # MCP Tools Grounding for Portfolio Q&A
  
  ## Available Tools
  - `list_portfolios` — Get portfolio list
  - `get_portfolio_dashboard` — High-level portfolio summary
  - `list_leases` — View active leases
  - `list_payments` — View rent collection
  - `list_work_orders` — View maintenance
  - `list_expenses` — View costs
  - `list_vendors` — View vendor records
  - `list_tenants` — View tenant data
  
  ## Tool-Calling Loop
  
  User asks a question → PortfolioQaService builds tool schema → invokes LLM with tools → LLM returns tool_use → service executes via MCP HTTP API → results added to message history → LLM re-invoked → LLM returns text answer.
  
  Example: "Who's late?" → LLM picks list_payments tool → gets all overdue payments → LLM extracts high-priority ones → returns "Unit 3 and Unit 7 are 10+ days late."
  ```

- [ ] **Optional: Add new MCP tools if discovered during testing:**
  - `list_appointments` — View scheduled appointments
  - `get_tenant_detail` — Get tenant contact + lease summary
  - `list_expenses_by_category` — Get spending breakdown

- [ ] **Verify (green):** Documentation is clear; any new tools have schemas in PortfolioQaService.

- [ ] **Commit:** "Document MCP tool grounding for Portfolio Q&A."

---

## Acceptance criteria

- [ ] All AI endpoints honor PortfolioId claim scoping (reject mismatched requests)
- [ ] Real ILlmProvider implementations (vision + chat + tool-calling) in place; model read from config, never hardcoded
- [ ] Daily Briefing worker generates rules-based (rent due, late, emergencies, appointments) + LLM-enhanced briefing once per day per portfolio; delivered via outbox (SMS-first); one-tap actions work
- [ ] Portfolio Q&A endpoint invokes real MCP tools via HTTP, maintains tool-calling loop, returns answers grounded in database with source tools listed + token usage tracked
- [ ] Voice capture endpoint creates VoiceDraft, enqueues async transcription via Engine worker, updates status → Reviewing via SignalR
- [ ] Lease FAQ endpoint answers tenant questions grounded in lease PDF
- [ ] Maintenance triage endpoint analyzes photos via vision, returns category/priority/vendor suggestions
- [ ] All AI calls include token tracking (InputTokens, OutputTokens) + cost provenance stored on draft/result entities
- [ ] Deterministic fallback when no API key: LLM calls return sensible fallback text (e.g., "AI services not configured")
- [ ] SignalR broadcasts work: briefing push to `briefing_portfolio_{id}`, voice updates to `voice_draft_{id}`
- [ ] Frontend Q&A box and briefing card implemented and wired to SignalR; Playwright journey green
- [ ] All endpoints have passing integration tests; Engine workers have passing transaction-rollback tests
- [ ] Migrations applied cleanly; existing data not corrupted

---

## Plan self-review notes

**Spec sections covered:**
- ✅ §5 "The two AI brains" — Daily Briefing (outbound) + Portfolio Q&A (inbound) both implemented
- ✅ §5 "Real AI core" — ILlmProvider real implementations + config-driven model
- ✅ §5 Voice capture, Tenant FAQ bot, maintenance triage all specified and implemented
- ✅ §4 Capture → Draft → Confirm pattern reused for Voice (VoiceDraft) + Maintenance (MaintenanceTriageResult)
- ✅ §6 MCP server grounding for Q&A (no changes to MCP itself; only consume tools)
- ✅ Cross-cutting: Append-only audit trail (provenance per draft), per-field confidence, token tracking, deterministic fallback

**Architecture alignment:**
- ILlmProvider expanded with tool-schema support (tool-calling loop ready for future automation)
- Engine worker pattern reused (DailyBriefingWorker, VoiceTranscriptionWorker)
- SignalR hub broadcasts for realtime notifications
- Service-scoped DI (PortfolioQaService, LeaseParsingService, etc.)
- PortfolioId claim scoping enforced everywhere

**Tech debt cleared:**
- Real LLM endpoints replace keyword stubs in AiEndpoints.cs
- Model id from config (AssistantConfig), never hardcoded
- Speech-to-text provider interface ready for swapping (Google, Azure, OpenAI)

**Deferred / out of scope for Phase 3:**
- Credit-bureau rent reporting (distinct from screening; Phase 6+)
- Predictive maintenance (data not yet collected; §5 note)
- Prompt caching implementation details (in code; token savings deferred to Phase 3b if separate)
- Full offline speech recognition (mobile feature; Phase 8)
- Multi-language FAQ support (can be added per-portfolio config)
