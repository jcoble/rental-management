# Lease Templates: Generic → State-Specific + Required Disclosures — Implementation Plan

> **For agentic workers:** Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the generated residential-lease PDF state-aware: vary the statutory clauses
(security-deposit return deadline, landlord-entry notice, late-fee posture, governing law)
by the property's state, append federally-required disclosures (lead-based paint for
pre-1978 properties), and surface a clear "not legal advice" disclaimer in the document.

**Architecture:** Pure, data-driven additions inside the existing
`LeaseAgreementPdfGenerator`. A small, static `StateLeaseRules` lookup keyed by the lease's
already-present two-letter `State` provides per-state statutory parameters; the operating
state (Ohio) is populated with real, sourced figures, a handful of common states are
included, and every unknown state falls back to a safe generic profile that defers to
"applicable law." A pre-1978 `YearBuilt` triggers the federal lead-paint disclosure block.
**No database migration, no schema change** — `Property.State` and `Property.YearBuilt`
already exist and already flow into `LeaseAgreementData`; we only add `YearBuilt` to the
DTO and read it in the generator.

**Tech Stack:** .NET 10, C#, QuestPDF (PDF rendering), xUnit + FluentAssertions (tests).

**Disclaimer / legal note:** Clause text is convenience boilerplate built from publicly
documented statutory parameters; it is **not legal advice**. Sources for the seeded
parameters are cited inline in `StateLeaseRules`.

**DEFERRED (explicitly out of scope):** "let the landlord upload / customize their own
lease template." That requires file storage for templates + a DB migration and a
data-model change, which violates the no-migration guardrail. Tracked as remaining work,
not implemented here.

---

## File Structure

- **Modify** `RentalCommand.Api/Services/Domain/LeaseAgreementPdfGenerator.cs`
  - Add `int? YearBuilt` to `LeaseAgreementData`.
  - Add a private nested `StateLeaseRules` record + static lookup (OH seeded with real
    figures; a few common states; generic fallback).
  - Render the governing-law clause with the real state name (not "State of XX").
  - Use state rules to phrase the security-deposit, late-charges, and entry clauses.
  - Append a "Required Disclosures" section: federal lead-paint block when pre-1978;
    always render an explicit in-body disclaimer paragraph.
- **Modify** `RentalCommand.Api/Services/Domain/LeaseService.cs` — set `YearBuilt` on the
  `LeaseAgreementData` it builds.
- **Modify** `RentalCommand.Api/Services/Esign/NativeSigningService.cs` — set `YearBuilt`
  on the `LeaseAgreementData` it builds (second call site).
- **Modify** `RentalCommand.Api.Tests/Domain/LeaseAgreementDocumentTests.cs` — add unit
  tests for clause/disclosure selection by extracting rendered text from the PDF, or by
  testing the rule lookup directly via an exposed pure helper.

---

## Design: `StateLeaseRules`

A pure record describing the parameters the PDF needs. No I/O, no DB.

```csharp
internal sealed record StateLeaseRules(
    string StateCode,            // "OH"
    string StateName,            // "Ohio"
    string DepositReturnText,    // sentence describing return deadline / interest
    string EntryNoticeText,      // sentence describing landlord-entry notice
    string LateFeeText,          // sentence describing late-fee posture
    string? StatuteCitation);    // e.g. "Ohio Rev. Code Ch. 5321" or null for generic
```

Lookup (`StateLeaseRules.For(string? stateCode)`):
- Normalizes input (trim, upper, first 2 chars).
- Returns the seeded rule for known states; otherwise a **generic** rule whose sentences
  defer to "applicable law" and whose `StateName` is the raw code (so governing-law still
  names the jurisdiction) or "the state where the Premises are located" when blank.

**Seeded data (sourced):**
- **OH (operating state) — Ohio Rev. Code Ch. 5321:**
  - Deposit: no statutory cap; itemized deductions + balance returned within **30 days**
    after termination and delivery of possession; deposits held > 6 months that exceed
    $50 or one month's rent accrue **5% annual interest** (ORC 5321.16).
  - Entry: at least **24 hours** reasonable notice, except emergencies (ORC 5321.04(A)(8)).
  - Late fee: no statutory cap; must be reasonable.
- A small set of additional common states with their headline figures (deposit return
  deadline + entry notice), each citing its statute, so non-Ohio properties read sensibly.
- **Generic fallback:** every clause defers to "applicable law"; matches today's behavior
  but with the real state name in governing law.

**Lead-paint disclosure:** federal, not per-state. Triggered when
`YearBuilt is not null && YearBuilt < 1978`. Renders the federal Lead Warning Statement +
a note that the EPA/HUD pamphlet "Protect Your Family From Lead in Your Home" must be
provided. (Source: 24 CFR Part 35 / 40 CFR Part 745, Residential Lead-Based Paint Hazard
Reduction Act §1018.) Properties with unknown or post-1978 `YearBuilt` get no lead block.

---

## Tasks

### Task 1: Add `YearBuilt` to the data DTO and both call sites

**Files:**
- Modify: `RentalCommand.Api/Services/Domain/LeaseAgreementPdfGenerator.cs` (`LeaseAgreementData`)
- Modify: `RentalCommand.Api/Services/Domain/LeaseService.cs:350-359`
- Modify: `RentalCommand.Api/Services/Esign/NativeSigningService.cs:347-356`

- [ ] **Step 1: Add the property to `LeaseAgreementData`**

```csharp
/// <summary>Year the property was built; drives the federal pre-1978 lead-paint disclosure. Null = unknown.</summary>
public int? YearBuilt { get; init; }
```

- [ ] **Step 2: Set it in `LeaseService.GenerateDocumentAsync`** — add `YearBuilt = property?.YearBuilt,` to the `LeaseAgreementData` initializer.

- [ ] **Step 3: Set it in `NativeSigningService.BuildExecutedDataAsync`** — add `YearBuilt = property?.YearBuilt,` to the `LeaseAgreementData` initializer.

- [ ] **Step 4: Build** — `dotnet build` succeeds.

### Task 2: Add `StateLeaseRules` + selection logic (with tests first)

**Files:**
- Modify: `RentalCommand.Api/Services/Domain/LeaseAgreementPdfGenerator.cs`
- Test: `RentalCommand.Api.Tests/Domain/LeaseAgreementDocumentTests.cs` (new test methods)

- [ ] **Step 1: Write failing tests** for `StateLeaseRules.For`:
  - `For("OH")` → `StateName == "Ohio"`, deposit text mentions "30 days", entry text mentions "24 hours", citation mentions "5321".
  - `For("oh ")` (lower/space) normalizes to the OH rule.
  - `For("ZZ")` → generic: `StatuteCitation` is null and `StateName == "ZZ"`.
  - `For("")` / `For(null)` → generic with a "the state where the Premises are located" style name and null citation.

- [ ] **Step 2: Run tests, confirm they fail to compile** (type not defined).

- [ ] **Step 3: Implement** the `StateLeaseRules` record + `For(...)` lookup + seeded data described above. Make it `internal` and expose it to the test project (the test assembly already references the API project; mark with `internal` and add `[assembly: InternalsVisibleTo("RentalCommand.Api.Tests")]` if not already present, or make the record `public`). Prefer the minimal change that compiles cleanly.

- [ ] **Step 4: Run tests, confirm they pass.**

### Task 3: Render state-aware clauses + disclosures + disclaimer

**Files:**
- Modify: `RentalCommand.Api/Services/Domain/LeaseAgreementPdfGenerator.cs` (`ComposeContent`)
- Test: `RentalCommand.Api.Tests/Domain/LeaseAgreementDocumentTests.cs`

- [ ] **Step 1: Write a failing test** that renders a PDF for a pre-1978 OH property,
  extracts the text, and asserts it contains the real state name "Ohio", a deposit
  return reference, a lead-paint warning phrase, and the in-body disclaimer phrase
  ("not legal advice"). Also a test that a 1990-built property's PDF does **not** contain
  the lead-paint warning phrase. Use a lightweight PDF text extraction (PdfPig is already
  a dependency in the codebase) or assert on a pure "build clause text" helper if text
  extraction proves brittle.

- [ ] **Step 2: Run, confirm fail.**

- [ ] **Step 3: Implement** — in `ComposeContent`:
  - Resolve `var rules = StateLeaseRules.For(data.State);`.
  - Late-charges clause: keep the captured amount, append `rules.LateFeeText`.
  - Security-deposit clause: keep the captured amount, use `rules.DepositReturnText`.
  - Entry clause: use `rules.EntryNoticeText`.
  - Governing-law clause: `"...laws of {rules.StateName}"` + optional
    `" (see {rules.StatuteCitation})"` when present.
  - After clause 13, add a **"Required Disclosures"** block:
    - Always: an explicit disclaimer paragraph ("This template is provided for
      convenience and is not legal advice...").
    - If pre-1978: the federal Lead Warning Statement + pamphlet note.

- [ ] **Step 4: Run all tests, confirm pass.**

- [ ] **Step 5: Commit.**

### Task 4: Verify + review

- [ ] `dotnet build` (clean).
- [ ] `dotnet test` (all green). No `web/` changes → skip svelte-check/pnpm build.
- [ ] Code-review subagent; address findings.
- [ ] Commit, push, PR → main, squash-merge.

---

## Self-Review

- **Spec coverage:** state-specific clauses ✔ (deposit/entry/late-fee/governing-law via
  `StateLeaseRules`); real governing law instead of "State of XX" ✔; required disclosures
  ✔ (federal lead-paint, pre-1978); visible disclaimer ✔ (in-body + existing footer);
  no-migration ✔ (reuses `State` + `YearBuilt`). Custom-template upload ✖ — **deferred**.
- **Placeholders:** none — concrete text/figures sourced above.
- **Type consistency:** `StateLeaseRules` shape and `For(...)` signature used consistently
  across tasks; `YearBuilt` named identically in DTO and both call sites.
