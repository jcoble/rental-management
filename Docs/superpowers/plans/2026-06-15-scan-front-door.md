# Implementation Plan — "Scan Front Door": guided, pre-filled, transparent New-Rental-from-your-lease flow (web + mobile)

**Date:** 2026-06-15
**Repo:** Rental Command (`RentalCommand.Api` + `RentalCommand.Engine`, SvelteKit `web/`, Flutter `mobile/`)
**Status:** Ready for sonnet sub-agent execution.

---

## 0. What this builds (locked design — do NOT relitigate)

A user scans **multiple phone photos strung together OR a PDF** of a lease → the existing AI extraction fills the fields → the app presents a **guided, explicitly-stepped, pre-filled, fully-editable flow** that reuses the **real create-form field groups**:

> **Step 1 of 4 · Property** → **Step 2 of 4 · Unit** → **Step 3 of 4 · Tenant** → **Step 4 of 4 · Lease** → **Review & confirm**

Each step renders the ACTUAL create form (a shared field component reused by the manual modals/sheets — not a lookalike), pre-filled from the lease extraction, fully editable; address fields use **Google Places autocomplete** (web `AddressAutocomplete.svelte`; new thin Flutter widget on the existing backend proxy). On the final Review screen — and only on the user's explicit confirm — the whole thing is created in ONE transaction via the existing `ScanService.ConfirmAsLeaseAsync` (web) / `scanRepository.confirm` (mobile). Mobile mirrors web. Receipts→Expense already works; do not rebuild it.

**Multi-photo strategy (decided):** client-side stitch the selected photos into a single PDF, then upload via the EXISTING single-file path (zero backend change — the server already handles PDF via PdfPig + a vision fallback). Web: `jspdf`. Mobile: `pdf` + `image` packages.

### First-class, testable acceptance criteria (apply to BOTH web Lane B and mobile Lane C)

> These are MANDATORY. Every UI step below restates the relevant AC. A lane is not "done" until all five hold.

- **AC-1 — Explicit step sequence.** The flow is a clearly-labeled, ordered set of steps with a visible progress indicator ("Step 2 of 4 · Unit") so the user ALWAYS knows which step they're on and what's left. One entity per step; advance with a clear **Next**; **Back** returns to the prior step preserving edits.
- **AC-2 — Reuse the real forms, pre-filled.** Each step renders the ACTUAL create form (the shared field component used by the manual modals/sheets — there must be exactly ONE property-form UI, ONE unit-form UI, etc.), already filled in from the lease extraction, fully editable.
- **AC-3 — Transparency ("see exactly what is being put in the system").** Nothing is created silently before the final confirm. At each step the user can clearly see exactly what will be entered. Auto-filled fields carry a subtle **"from your lease"** hint/badge so it's obvious what the scan provided vs. what the user typed.
- **AC-4 — Final review before save.** The flow ends with a summary/confirmation screen — *"Here's what I'll add: Property «Maple Apts, 123 Main St» · Unit «4B» · Tenant «Jane Smith» · Lease «$1,400/mo, Jun 1 – May 31»"* — and ONLY on the user's confirm is everything created (web: one `scan.confirm` → `ScanService.ConfirmAsLeaseAsync`). The duplicate-guard surfaces here AND at the Property step.
- **AC-5 — Mobile mirrors web.** The same stepped, labeled, pre-filled, see-everything flow exists on mobile (reusing the add-sheet field groups), not a single dense screen.

### Design-doc reconciliation (read before starting)

`Docs/Reviews/2026-06-15-ease-of-use-restructure-audit.md` items **B7/C2** earlier proposed "one confirm card, hide the Create-vs-Link toggle." **This plan supersedes that** with the stepped real-forms flow per the locked design + the AC above. After Lane B/C land, the controller updates the audit doc's B7 line to point at this plan (Lane A step A0 files that doc note). Mock 7 (`Docs/Reviews/2026-06-15-ease-of-use-mockups/README.md`, rows 7a/7b) already specifies "real create forms PRE-FILLED + Google-Places address confirm + multi-page photo/PDF" — this plan implements exactly that.

---

## 1. Architecture facts this plan depends on (VERIFIED this session — file:line)

**Backend — already does the hard part; we extend it minimally:**
- `POST /api/v1/scans` accepts one `IFormFile file` + `targetEntityType` (`RentalCommand.Api/Controllers/ScanController.cs:53-94`). PDF is inline-safe (`:27-30`). It already handles PDFs (PdfPig text + vision fallback).
- `POST /api/v1/scans/{id}/confirm` takes `{ overridesJson }` (`ScanController.cs:515-540`).
- `GET /api/v1/scans/{id}` returns the draft + (for Lease) a `leaseProposal` (`ScanController.cs:377-405`).
- `ScanService.ConfirmAsLeaseAsync` (`RentalCommand.Api/Scanning/ScanService.cs:656-805`) is the ONE-transaction match-or-create: resolves/creates Property (`:856-891`), Unit (`:900-928`), Tenant (`:813-845`), then creates the Lease. The whole confirm is wrapped in a single DB transaction (`ConfirmAndCreateAsync:136-210`). Overrides applied via `ApplyLeaseOverrides` (`:1706-1774`) — it already accepts every Property/Unit/Tenant/Lease field as a camelCase or snake_case override key. **No backend change is needed to finalize the stepped flow — the existing override contract already carries everything the four steps collect.**
- Lease extraction schema (`RentalCommand.Api/Scanning/LeaseExtractionSchema.cs:19-88`, promptId `lease-pdf-to-lease-v2`) returns 20 fields spanning Property + Unit + Tenant + Lease.
- Google Places backend EXISTS and works: `GET /api/v1/places/autocomplete?q=&session=` and `GET /api/v1/places/details?placeId=&session=` (`RentalCommand.Api/Controllers/PlacesController.cs:21-163`), key server-side via `GooglePlacesConfig` (`RentalCommand.Core/Configuration/GooglePlacesConfig.cs`), graceful `enabled:false` when no key. The details endpoint returns `{ line1, city, state, zip }` (`GooglePlacesService.cs:102-140`).

**Web — VERIFIED form shapes:**
- Zod schemas (`web/src/lib/schemas/index.ts`): `propertySchema:135-146` `{name,type,addressLine1,addressLine2?,city,state,postalCode,ownerEntityId?}`; `unitSchema:148-155` `{unitNumber,bedrooms,bathrooms,marketRent}`; `tenantSchema:157-163` `{firstName,lastName,email?,phone?,emergencyContact?}`; `leaseSchema:165-185` `{leaseNumber,propertyId,unitId,tenantId,startDate,endDate,moveInDate?,monthlyRent,securityDeposit,lateFeeAmount,rentDueDay,status,notes?}`. `parseForm(schema,input)` → `{data}|{errors}` (`:391-405`).
- Manual modals (each is self-contained `$state` form + `formErrors` + `parseForm` + create mutation, NO `initialValues` prop today): Property `web/src/routes/(protected)/properties/+page.svelte` (`emptyProperty:77`, `submitProperty:117`, AddressAutocomplete `:340-350`); Unit modal INSIDE `web/src/routes/(protected)/properties/[id]/+page.svelte` (`emptyUnit:141`, modal `:586-623`, `submitUnit`); Tenant `web/src/routes/(protected)/tenants/+page.svelte` (`empty:46`, modal `:230-257`); Lease `web/src/routes/(protected)/leases/+page.svelte` (`empty:62-70`, modal `:282-375`, unit list filtered by `formPropertyId`).
- `AddressAutocomplete.svelte:1-254` — `bind:value` + `onresolved({line1,city,state,zip})`, self-disables when server returns `enabled:false`.
- `FileDrop.svelte:84-91` — single file (`<input type="file">`, NO `multiple`).
- Scan client `web/src/lib/api/scan.ts` — `scan.upload(file,target)`, `scan.get(id)`, `scan.confirm(id,overridesJson)`.
- `LeasePhotoPrefill.svelte` — onboarding-only snap→`scan.upload(file,'Lease')`→poll→review→`onapply(values)`; only 7 scalar TERM_FIELDS (`:35-43`, drops property/unit/tenant). Mounted at `onboarding/+page.svelte:1128` via `applyLeasePrefill`.
- Existing scan review page `web/src/routes/(protected)/scan/[draftId]/+page.svelte` (1449 lines) ALREADY builds the full lease `overridesJson` (`buildOverridesJson:626-711`) feeding `scan.confirm` (`confirmMutation:571`), with a "When you confirm, this lease will: …" proposal block (`:944-967`) and editable create-property fields (`:997-1015`) + editable lease terms (`:1071-1097`). **This is the canonical reference for the override shape the new flow must emit.** No `forms/` shared dir exists yet (`web/src/lib/components/forms/` absent).

**Mobile — VERIFIED:**
- `mobile/lib/features/scan/scan_review_screen.dart` (2474 lines): `_fieldGroups:75-104`; create-vs-link `SegmentedButton:1364-1380`; `_CreatePropertyFields:1539-1583` (only 3 fields: property_address/property_name/property_city — NO state/zip/beds/baths/Places); `_LinkPropertyFields:1452-1532`; `buildOverridesMap:248-300` (the mobile analog of the web override builder — already sends propertyId/propertyName/propertyAddress/propertyCity/unitId/tenantId/lease terms); confirm `scanRepository.confirm(draft.id, overrides)`.
- `mobile/lib/features/capture/capture_fab_sheet.dart`: 5 options; `_pickImage:74-84` SINGLE image (NOT pickMultiImage); `_pickFile:86-102` single file; comment `:99-100` "multi-page PDF assembly … is P1"; upload via `scanRepository.uploadImage(bytes,filename,contentType)`.
- `mobile/lib/features/scan/scan_repository.dart`: `uploadImage(Uint8List,filename,contentType,{targetEntityType='Expense'}):70-108` (Dio `FormData`); `confirm(id,overrides):180-193`; provider uses `dioProvider`.
- Add-sheets: Property `_AddPropertySheet` `properties/properties_list_screen.dart:339-547` (FULL form: name/type/address/city/state/zip; createProperty `:389-396`; NO `existing` ctor); Unit `_AddUnitSheet` `properties/property_detail_screen.dart:564-762` (number/beds/baths/rent/status; NO prefill) + `_EditUnitSheet:766-982` (prefills in `initState:791-804`); Tenant `TenantFormSheet` `tenants/tenants_list_screen.dart:396-598` (ctor `existing: Tenant?:397`, seeds in `initState:421-429`); Lease `LeaseFormSheet` `leases/leases_list_screen.dart:369-722` (ctor `existing: Lease?:370`, seeds `:404-425`, loads property/tenant/unit pickers).
- `mobile/pubspec.yaml`: SDK `^3.12.0`; HAS `image_picker:^1.1.2`, `dio:^5.9.2`, `flutter_riverpod:^3.3.1`, `go_router:^17.2.3`, `file_picker:^8.1.7`, `path_provider:^2.1.5`. **NO `pdf`, NO `image`, NO google places package.**
- API client: `mobile/lib/core/api/dio_client.dart` (`dioProvider`), base URL `mobile/lib/core/config/app_config.dart`, auth via `auth_interceptor.dart` (Bearer attached automatically). Repositories take the shared `Dio`; add a new GET as `_dio.get<...>('/places/autocomplete', queryParameters: {...})`.
- State: Riverpod `NotifierProvider`; sheets opened via `showModalBottomSheet(isScrollControlled:true)`.

**Sample data:** committed `Docs/sample-scans/` is receipts/checks only (NO lease) — Lane A adds one. Local big-test corpus exists at `~/rental-sample-scans/` (32 docs) with `leases/` (7 lease PDFs + a `strung-photos-delgado` folder of phone photos of ONE lease).

---

## 2. FROZEN CONTRACTS (define FIRST; all lanes import these — do not change after lanes start)

These are the seams that let Lanes A/B/C run concurrently without merge conflicts. They are authored in **Step A1** (TS) / **Step A2** (Dart) before Lanes B/C begin coding their consumers. Once committed they are FROZEN.

### Contract 1 — The shared lease-prefill DTO (the bridge from extraction → the four pre-filled forms)

The scan extraction produces a flat `ScanFieldDto[]` (`{name,value,confidence}`). Both clients map it into a single typed object that seeds the four step-forms. The field names are exactly the `LeaseExtractionSchema` names.

**TypeScript** — NEW file `web/src/lib/scan/lease-prefill.ts`:

```ts
import type { ScanFieldDto } from '$lib/api/scan';

/**
 * Flat, typed view of a lease draft's extracted fields, used to PRE-FILL the four guided
 * create-step forms (Property → Unit → Tenant → Lease). Field names mirror LeaseExtractionSchema.cs.
 * `confidence` per field drives the subtle "from your lease" badge (AC-3).
 */
export interface LeasePrefill {
	// Property
	propertyId: string;          // extracted in-portfolio id, "" if none
	propertyName: string;
	propertyAddress: string;     // street line only
	propertyCity: string;
	propertyState: string;       // 2-letter
	propertyPostalCode: string;
	// Unit
	unitId: string;
	unitNumber: string;
	unitBedrooms: string;
	unitBathrooms: string;
	unitSquareFeet: string;
	// Tenant (free text — no tenant_id in schema)
	tenantName: string;
	// Lease terms
	leaseNumber: string;
	startDate: string;           // YYYY-MM-DD
	endDate: string;             // YYYY-MM-DD
	monthlyRent: string;
	securityDeposit: string;
	lateFee: string;
	rentDueDay: string;
}

/** Field name → confidence (0..1), for the "from your lease" badge. Absent name => not auto-filled. */
export type PrefillConfidence = Record<string, number>;

const FIELD = (fs: Map<string, ScanFieldDto>, name: string) => (fs.get(name)?.value ?? '').trim();

/** Build the typed prefill + a confidence map from a draft's raw extracted fields. */
export function toLeasePrefill(fields: ScanFieldDto[]): { values: LeasePrefill; confidence: PrefillConfidence } {
	const fs = new Map(fields.map((f) => [f.name, f]));
	const confidence: PrefillConfidence = {};
	for (const f of fields) if ((f.value ?? '').trim()) confidence[f.name] = f.confidence;
	const values: LeasePrefill = {
		propertyId: FIELD(fs, 'property_id'),
		propertyName: FIELD(fs, 'property_name'),
		propertyAddress: FIELD(fs, 'property_address'),
		propertyCity: FIELD(fs, 'property_city'),
		propertyState: FIELD(fs, 'property_state'),
		propertyPostalCode: FIELD(fs, 'property_postal_code'),
		unitId: FIELD(fs, 'unit_id'),
		unitNumber: FIELD(fs, 'unit_number'),
		unitBedrooms: FIELD(fs, 'unit_bedrooms'),
		unitBathrooms: FIELD(fs, 'unit_bathrooms'),
		unitSquareFeet: FIELD(fs, 'unit_square_feet'),
		tenantName: FIELD(fs, 'tenant_name'),
		leaseNumber: FIELD(fs, 'lease_number'),
		startDate: FIELD(fs, 'start_date'),
		endDate: FIELD(fs, 'end_date'),
		monthlyRent: FIELD(fs, 'monthly_rent'),
		securityDeposit: FIELD(fs, 'security_deposit'),
		lateFee: FIELD(fs, 'late_fee'),
		rentDueDay: FIELD(fs, 'rent_due_day')
	};
	return { values, confidence };
}

/** Map a field name in a step-form to the extraction key that fills it (for the "from your lease" badge). */
export const STEP_FIELD_TO_EXTRACTION: Record<string, string> = {
	// property form field -> extraction name
	name: 'property_name',
	addressLine1: 'property_address',
	city: 'property_city',
	state: 'property_state',
	postalCode: 'property_postal_code',
	// unit
	unitNumber: 'unit_number',
	bedrooms: 'unit_bedrooms',
	bathrooms: 'unit_bathrooms',
	// tenant
	firstName: 'tenant_name',
	lastName: 'tenant_name',
	// lease
	leaseNumber: 'lease_number',
	startDate: 'start_date',
	endDate: 'end_date',
	monthlyRent: 'monthly_rent',
	securityDeposit: 'security_deposit',
	lateFeeAmount: 'late_fee',
	rentDueDay: 'rent_due_day'
};
```

**Dart** — NEW file `mobile/lib/features/scan/lease_prefill.dart`:

```dart
/// Typed view of a lease draft's extracted fields used to PRE-FILL the four guided
/// create-step sheets (Property -> Unit -> Tenant -> Lease). Names mirror LeaseExtractionSchema.cs.
class LeasePrefill {
  LeasePrefill({
    this.propertyId = '',
    this.propertyName = '',
    this.propertyAddress = '',
    this.propertyCity = '',
    this.propertyState = '',
    this.propertyPostalCode = '',
    this.unitId = '',
    this.unitNumber = '',
    this.unitBedrooms = '',
    this.unitBathrooms = '',
    this.unitSquareFeet = '',
    this.tenantName = '',
    this.leaseNumber = '',
    this.startDate = '',
    this.endDate = '',
    this.monthlyRent = '',
    this.securityDeposit = '',
    this.lateFee = '',
    this.rentDueDay = '',
  });

  final String propertyId;
  final String propertyName;
  final String propertyAddress;
  final String propertyCity;
  final String propertyState;
  final String propertyPostalCode;
  final String unitId;
  final String unitNumber;
  final String unitBedrooms;
  final String unitBathrooms;
  final String unitSquareFeet;
  final String tenantName;
  final String leaseNumber;
  final String startDate;
  final String endDate;
  final String monthlyRent;
  final String securityDeposit;
  final String lateFee;
  final String rentDueDay;

  /// Build from the draft's extracted fields list ({name,value,confidence} maps) and
  /// the set of field-names that were auto-filled (non-blank), for the "from your lease" badge.
  static ({LeasePrefill values, Set<String> filled}) fromFields(
      List<Map<String, dynamic>> fields) {
    final byName = <String, String>{};
    final filled = <String>{};
    for (final f in fields) {
      final name = (f['name'] ?? '').toString();
      final value = (f['value'] ?? '').toString().trim();
      byName[name] = value;
      if (value.isNotEmpty) filled.add(name);
    }
    String g(String k) => byName[k] ?? '';
    return (
      values: LeasePrefill(
        propertyId: g('property_id'),
        propertyName: g('property_name'),
        propertyAddress: g('property_address'),
        propertyCity: g('property_city'),
        propertyState: g('property_state'),
        propertyPostalCode: g('property_postal_code'),
        unitId: g('unit_id'),
        unitNumber: g('unit_number'),
        unitBedrooms: g('unit_bedrooms'),
        unitBathrooms: g('unit_bathrooms'),
        unitSquareFeet: g('unit_square_feet'),
        tenantName: g('tenant_name'),
        leaseNumber: g('lease_number'),
        startDate: g('start_date'),
        endDate: g('end_date'),
        monthlyRent: g('monthly_rent'),
        securityDeposit: g('security_deposit'),
        lateFee: g('late_fee'),
        rentDueDay: g('rent_due_day'),
      ),
      filled: filled,
    );
  }
}
```

### Contract 2 — The shared field-component prop shapes (web)

Each guided step renders the SAME field component the manual modal uses. The components accept a bound form object + an error map + an optional set of "auto-filled field names" (drives the badge). Manual modals pass `autoFilled={undefined}` (badge off); the guided flow passes the set. Prop shapes (FROZEN):

```ts
// PropertyFields.svelte
{
  form: { name: string; type: string; addressLine1: string; addressLine2: string; city: string; state: string; postalCode: string; ownerEntityId: string };  // bindable
  errors?: Record<string, string>;
  autoFilled?: Set<string>;       // field keys auto-filled from the lease (badge); undefined = no badges
  testidPrefix?: string;          // default 'property'
}
// UnitFields.svelte
{ form: { unitNumber: string; bedrooms: string; bathrooms: string; marketRent: string }; errors?; autoFilled?; testidPrefix? /* 'unit' */ }
// TenantFields.svelte
{ form: { firstName: string; lastName: string; email: string; phone: string; emergencyContact: string }; errors?; autoFilled?; testidPrefix? /* 'tenant' */ }
// LeaseTermFields.svelte  (terms ONLY — property/unit/tenant are prior steps; no pickers here)
{ form: { leaseNumber: string; startDate: string; endDate: string; monthlyRent: string; securityDeposit: string; lateFeeAmount: string; rentDueDay: string; status: string; notes: string }; errors?; autoFilled?; testidPrefix? /* 'lease' */ }
```

> The components own ONLY the field rows (labels + inputs + inline `formErrors` + the optional badge). They do NOT own the Dialog chrome or the submit button — the modal and the guided flow each provide their own wrapper. This is what makes "ONE property-form UI" true while keeping both call sites working.

### Contract 3 — The guided-flow → confirm override JSON (web)

The guided flow finalizes by emitting EXACTLY the lease override shape the existing review page already sends to `ScanService.ConfirmAsLeaseAsync` (verified `buildOverridesJson:643-668`). Frozen keys:

```jsonc
{
  // Property: either link an existing id, or create-new (id=null → server match-or-create from these)
  "propertyId": 0,                 // number id to LINK, or null to CREATE
  "propertyName": "Maple Apts",
  "propertyAddress": "123 Main St",
  "propertyCity": "Columbus",
  "propertyState": "OH",
  "propertyPostalCode": "43004",
  // Unit: link id, or omit/null to create under the (new or chosen) property
  "unitId": 0,                     // number id to LINK, or null to CREATE
  "unitNumber": "4B",
  "unitBedrooms": 2,
  "unitBathrooms": 1.5,
  "unitSquareFeet": 950,
  // Tenant: link id, or omit to match/create by name
  "tenantId": 0,                   // number id to LINK; OMIT to create from tenantName
  "tenantName": "Jane Smith",
  // Lease terms
  "leaseNumber": "L-1001",
  "startDate": "2026-06-01",
  "endDate": "2027-05-31",
  "monthlyRent": 1400,
  "securityDeposit": 1400,
  "lateFee": 75,
  "rentDueDay": 1
}
```

`ApplyLeaseOverrides` (`ScanService.cs:1706-1774`) already accepts every one of these keys. **No backend change needed** for this contract; `unitBedrooms`/`unitBathrooms`/`unitSquareFeet` are already consumed (`:1739-1744`).

### Contract 4 — Mobile Places repository + widget API (FROZEN)

- New repository method (added to a NEW `mobile/lib/features/places/places_repository.dart`):
  - `Future<List<PlaceSuggestion>> autocomplete(String query, String session)` → `GET /places/autocomplete?q=&session=`
  - `Future<ResolvedAddress?> details(String placeId, String session)` → `GET /places/details?placeId=&session=`
  - DTOs: `PlaceSuggestion(placeId, primary, secondary)`, `ResolvedAddress(line1, city, state, zip)` — mirror `GooglePlacesService.cs:8-11`. Backend autocomplete returns `{ enabled, suggestions: [{placeId,primary,secondary}] }`; treat `enabled:false` as "no suggestions, stay manual."
- New widget `mobile/lib/features/places/address_autocomplete_field.dart`:
  - Props: `TextEditingController controller` (the street line), `void Function(ResolvedAddress)? onResolved`, `String hintText = 'Street address'`, `String? testKey`.
  - Behavior mirrors web: debounce 250ms, min 3 chars, dropdown of suggestions, on pick fill controller + call `onResolved` with city/state/zip; silently degrade to a plain field on any error or `enabled:false`. NO Google SDK, NO client key.

### Contract 5 — multi-file → single-PDF stitch (both)

- Web util (NEW `web/src/lib/scan/stitch-pdf.ts`): `stitchImagesToPdf(files: File[]): Promise<File>` — each image becomes one page (auto portrait/landscape, fit-to-page); returns a single `application/pdf` `File` named `lease-scan.pdf`. One image in → one-page PDF.
- Mobile util (NEW `mobile/lib/features/scan/pdf_stitch.dart`): `Future<Uint8List> stitchImagesToPdf(List<Uint8List> images)` — same, one image per page, orientation-aware.
- Both then upload through the EXISTING single-file path (`scan.upload(file,'Lease')` / `scanRepository.uploadImage(...)`). Zero backend change.

---

## 3. Lanes & file ownership (parallel-safe)

Run Lane A FIRST through **A2** (contracts) — Lanes B and C consume those files. After A2 is committed, Lanes B and C run **concurrently**; Lane A's remaining steps (A3 sample doc, A0 doc note) are independent and can run any time. **Per the project memory rule, do NOT run two `dotnet build`/`flutter`/`svelte-check` lanes at the same instant — serialize the build/verify command of each lane.** Coding/editing in parallel is fine; gate the actual compile steps.

| Lane | Owns (exclusive files) | Verify gate |
|---|---|---|
| **A — Contracts + backend-adjacent + docs** | `web/src/lib/scan/lease-prefill.ts`, `web/src/lib/scan/stitch-pdf.ts`, `mobile/lib/features/scan/lease_prefill.dart`, `mobile/lib/features/scan/pdf_stitch.dart`, `mobile/lib/features/places/places_repository.dart`, `mobile/lib/features/places/address_autocomplete_field.dart`, `Docs/sample-scans/13-lease-sunset-ridge.*`, `Docs/sample-scans/README.md`, the B7 note in `Docs/Reviews/2026-06-15-ease-of-use-restructure-audit.md` | web: `cd web && npx svelte-check --threshold error`; mobile: `cd mobile && flutter analyze` |
| **B — Web** | `web/src/lib/components/forms/PropertyFields.svelte`, `UnitFields.svelte`, `TenantFields.svelte`, `LeaseTermFields.svelte`, `web/src/lib/components/forms/AutoFilledBadge.svelte`, the 4 manual modal pages (refactor to consume the field components), `web/src/routes/(protected)/scan/new-rental/+page.svelte` (the guided flow), `web/src/lib/components/FileDrop.svelte` (add `multiple`), `web/src/routes/(protected)/scan/+page.svelte` (entry), `web/src/lib/components/onboarding/LeasePhotoPrefill.svelte` (route into the guided flow) | `cd web && npx svelte-check --threshold error` (0 errors) |
| **C — Mobile** | `mobile/lib/features/scan/guided_rental_flow.dart` (the stepped flow), `mobile/lib/features/scan/scan_review_screen.dart` (extend `_CreatePropertyFields` + route lease drafts into the guided flow), `mobile/lib/features/capture/capture_fab_sheet.dart` (multi-image pick + stitch), the 4 add-sheets (swap address field → Places widget; accept a `LeasePrefill?`), `mobile/pubspec.yaml` (add `pdf`,`image`) | `cd mobile && flutter analyze` (0 issues) |

> Lane B touches the 4 manual web pages; Lane C touches the 4 mobile sheets. They never touch each other's files. Lane A's contract files are imported read-only by B and C. The ONLY shared-file risk is `mobile/pubspec.yaml` (Lane C only) and the audit doc (Lane A only) — both single-owner.

---

## LANE A — Contracts, utilities, mobile Places, sample doc, docs

### Step A0 — Reconcile the design-doc note (independent, do first or last)

**File:** `Docs/Reviews/2026-06-15-ease-of-use-restructure-audit.md`

Find the B7 heading line (`grep -n "B7. Lease scan-review" Docs/Reviews/2026-06-15-ease-of-use-restructure-audit.md` → ~line 131). Immediately under the `### B7.` heading block, append one line:

```md
> **Superseded 2026-06-15 by `Docs/superpowers/plans/2026-06-15-scan-front-door.md`:** the chosen design is NOT a one-tap confirm card — it is a guided, explicitly-stepped, pre-filled flow that reuses the real create forms (Property → Unit → Tenant → Lease) with a final review-before-save and a "from your lease" transparency badge. The Create-vs-Link choice is folded into the Property step's existing-vs-new toggle, not hidden.
```

**Verify:** `grep -n "Superseded 2026-06-15" Docs/Reviews/2026-06-15-ease-of-use-restructure-audit.md` → prints the line.
**Commit:** `docs(scan): note B7 superseded by the guided scan-front-door plan`

### Step A1 — Web contract files

Create `web/src/lib/scan/lease-prefill.ts` with the **exact** content from Contract 1 (TypeScript) above.
Create `web/src/lib/scan/stitch-pdf.ts`:

```ts
/**
 * Client-side stitch of selected lease photos into ONE PDF, then uploaded via the existing
 * single-file /scans path (the server already handles PDF). One image per page, fit-to-page,
 * orientation chosen per image. jspdf is the chosen lib: tiny, no worker, pure-client, already
 * image-oriented (we only need raster pages, not text). Photos of ONE document → one draft.
 */
import { jsPDF } from 'jspdf';

async function readAsDataUrl(file: File): Promise<string> {
	return new Promise((resolve, reject) => {
		const r = new FileReader();
		r.onload = () => resolve(r.result as string);
		r.onerror = () => reject(r.error);
		r.readAsDataURL(file);
	});
}

function loadImage(dataUrl: string): Promise<HTMLImageElement> {
	return new Promise((resolve, reject) => {
		const img = new Image();
		img.onload = () => resolve(img);
		img.onerror = () => reject(new Error('Could not decode image'));
		img.src = dataUrl;
	});
}

/** Stitch image files (jpeg/png/webp) into a single A4 PDF, one image per page. */
export async function stitchImagesToPdf(files: File[]): Promise<File> {
	if (files.length === 0) throw new Error('No photos to combine');
	let doc: jsPDF | null = null;
	for (const file of files) {
		const dataUrl = await readAsDataUrl(file);
		const img = await loadImage(dataUrl);
		const landscape = img.width > img.height;
		const orientation = landscape ? 'landscape' : 'portrait';
		if (doc === null) {
			doc = new jsPDF({ orientation, unit: 'pt', format: 'a4' });
		} else {
			doc.addPage('a4', orientation);
		}
		const pageW = doc.internal.pageSize.getWidth();
		const pageH = doc.internal.pageSize.getHeight();
		// Fit the image inside the page preserving aspect ratio, with a small margin.
		const margin = 18;
		const maxW = pageW - margin * 2;
		const maxH = pageH - margin * 2;
		const scale = Math.min(maxW / img.width, maxH / img.height);
		const w = img.width * scale;
		const h = img.height * scale;
		const x = (pageW - w) / 2;
		const y = (pageH - h) / 2;
		const fmt = file.type === 'image/png' ? 'PNG' : 'JPEG';
		doc.addImage(dataUrl, fmt, x, y, w, h);
	}
	const blob = doc!.output('blob');
	return new File([blob], 'lease-scan.pdf', { type: 'application/pdf' });
}
```

Add the dependency:

```bash
cd /Users/blackcolours/dev/work/rental-management/web && npm install jspdf@^2.5.2
```

**Verify:** `cd web && npx svelte-check --threshold error` → 0 errors. Also confirm import resolves: `node -e "require('jspdf')"` (expects no error) — if `node -e` fails because it's ESM-only, skip; svelte-check is authoritative.
**Commit:** `feat(scan-web): lease-prefill DTO + jspdf image-stitch util (frozen contracts)`

### Step A2 — Mobile contract files (Places repo + widget, prefill DTO, PDF stitch)

Add deps to `mobile/pubspec.yaml` is owned by Lane C (it also adds them); to avoid a double-edit, **Lane A adds `pdf` + `image` here** and Lane C does NOT touch pubspec. Update the `dependencies:` block (after `path_provider: ^2.1.5`) to add:

```yaml
  pdf: ^3.11.1
  image: ^4.3.0
```

Run: `cd /Users/blackcolours/dev/work/rental-management/mobile && flutter pub get`

Create `mobile/lib/features/scan/lease_prefill.dart` with the **exact** Dart content from Contract 1.

Create `mobile/lib/features/scan/pdf_stitch.dart`:

```dart
import 'dart:typed_data';
import 'package:image/image.dart' as img;
import 'package:pdf/pdf.dart';
import 'package:pdf/widgets.dart' as pw;

/// Stitch lease photos into ONE PDF (one image per page, orientation per image), uploaded via the
/// existing single-file /scans path. The server already handles PDF (PdfPig + vision fallback), so
/// this is zero-backend-change multi-photo intake. We re-encode each photo to JPEG (q85) so a HEIC
/// or oversized capture lands as a predictable, embeddable raster.
Future<Uint8List> stitchImagesToPdf(List<Uint8List> images) async {
  if (images.isEmpty) {
    throw ArgumentError('No photos to combine');
  }
  final doc = pw.Document();
  for (final bytes in images) {
    final decoded = img.decodeImage(bytes);
    if (decoded == null) {
      // Skip an undecodable frame rather than failing the whole stitch.
      continue;
    }
    final jpeg = Uint8List.fromList(img.encodeJpg(decoded, quality: 85));
    final memImage = pw.MemoryImage(jpeg);
    final landscape = decoded.width > decoded.height;
    doc.addPage(
      pw.Page(
        pageFormat: landscape ? PdfPageFormat.a4.landscape : PdfPageFormat.a4,
        margin: const pw.EdgeInsets.all(18),
        build: (context) => pw.Center(
          child: pw.Image(memImage, fit: pw.BoxFit.contain),
        ),
      ),
    );
  }
  return doc.save();
}
```

Create `mobile/lib/features/places/places_repository.dart`:

```dart
import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import '../../core/api/dio_client.dart';

/// One address suggestion (mirrors RentalCommand.Api PlaceSuggestion).
class PlaceSuggestion {
  const PlaceSuggestion({required this.placeId, required this.primary, required this.secondary});
  final String placeId;
  final String primary;
  final String secondary;

  factory PlaceSuggestion.fromJson(Map<String, dynamic> j) => PlaceSuggestion(
        placeId: (j['placeId'] ?? '').toString(),
        primary: (j['primary'] ?? '').toString(),
        secondary: (j['secondary'] ?? '').toString(),
      );
}

/// A resolved address split into the fields the forms own (mirrors ResolvedAddress).
class ResolvedAddress {
  const ResolvedAddress({required this.line1, required this.city, required this.state, required this.zip});
  final String line1;
  final String city;
  final String state;
  final String zip;

  factory ResolvedAddress.fromJson(Map<String, dynamic> j) => ResolvedAddress(
        line1: (j['line1'] ?? '').toString(),
        city: (j['city'] ?? '').toString(),
        state: (j['state'] ?? '').toString(),
        zip: (j['zip'] ?? '').toString(),
      );
}

/// Thin client over the EXISTING server-side Google Places proxy. The API key stays server-side;
/// this only calls /places/autocomplete and /places/details. Any failure (incl. enabled:false)
/// returns empty/null so the field degrades to plain manual entry.
class PlacesRepository {
  PlacesRepository({required Dio dio}) : _dio = dio;
  final Dio _dio;

  Future<List<PlaceSuggestion>> autocomplete(String query, String session) async {
    if (query.trim().length < 3) return const [];
    try {
      final res = await _dio.get<Map<String, dynamic>>(
        '/places/autocomplete',
        queryParameters: {'q': query, 'session': session},
      );
      final data = res.data;
      if (data == null || data['enabled'] != true) return const [];
      final list = (data['suggestions'] as List<dynamic>? ?? const []);
      return list
          .whereType<Map<String, dynamic>>()
          .map(PlaceSuggestion.fromJson)
          .toList(growable: false);
    } on DioException {
      return const [];
    }
  }

  Future<ResolvedAddress?> details(String placeId, String session) async {
    if (placeId.isEmpty) return null;
    try {
      final res = await _dio.get<Map<String, dynamic>>(
        '/places/details',
        queryParameters: {'placeId': placeId, 'session': session},
      );
      final data = res.data;
      if (data == null) return null;
      return ResolvedAddress.fromJson(data);
    } on DioException {
      return null;
    }
  }
}

final placesRepositoryProvider = Provider<PlacesRepository>((ref) {
  return PlacesRepository(dio: ref.watch(dioProvider));
});
```

Create `mobile/lib/features/places/address_autocomplete_field.dart`:

```dart
import 'dart:async';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'places_repository.dart';

/// Street-address field with optional Google Places autocomplete, mirroring the web
/// AddressAutocomplete.svelte behavior on the SAME server-side proxy (no client key, no SDK).
/// Manual entry always works; the dropdown only appears when the backend returns suggestions.
class AddressAutocompleteField extends ConsumerStatefulWidget {
  const AddressAutocompleteField({
    super.key,
    required this.controller,
    this.onResolved,
    this.hintText = 'Street address',
    this.label,
    this.testKey,
  });

  final TextEditingController controller;
  final void Function(ResolvedAddress address)? onResolved;
  final String hintText;
  final String? label;
  final String? testKey;

  @override
  ConsumerState<AddressAutocompleteField> createState() => _AddressAutocompleteFieldState();
}

class _AddressAutocompleteFieldState extends ConsumerState<AddressAutocompleteField> {
  final _layerLink = LayerLink();
  final _focusNode = FocusNode();
  OverlayEntry? _overlay;
  Timer? _debounce;
  List<PlaceSuggestion> _suggestions = const [];
  String _session = DateTime.now().microsecondsSinceEpoch.toString();
  int _seq = 0;

  @override
  void dispose() {
    _debounce?.cancel();
    _removeOverlay();
    _focusNode.dispose();
    super.dispose();
  }

  void _onChanged(String value) {
    _debounce?.cancel();
    if (value.trim().length < 3) {
      _setSuggestions(const []);
      return;
    }
    _debounce = Timer(const Duration(milliseconds: 250), () => _fetch(value));
  }

  Future<void> _fetch(String query) async {
    final mySeq = ++_seq;
    final results = await ref.read(placesRepositoryProvider).autocomplete(query, _session);
    if (!mounted || mySeq != _seq) return;
    _setSuggestions(results);
  }

  void _setSuggestions(List<PlaceSuggestion> s) {
    setState(() => _suggestions = s);
    if (s.isEmpty) {
      _removeOverlay();
    } else {
      _showOverlay();
    }
  }

  Future<void> _pick(PlaceSuggestion s) async {
    widget.controller.text = s.primary;
    _removeOverlay();
    final resolved = await ref.read(placesRepositoryProvider).details(s.placeId, _session);
    _session = DateTime.now().microsecondsSinceEpoch.toString(); // details closes the billing session
    if (resolved != null && mounted) {
      if (resolved.line1.isNotEmpty) widget.controller.text = resolved.line1;
      widget.onResolved?.call(resolved);
    }
  }

  void _showOverlay() {
    _removeOverlay();
    final overlay = OverlayEntry(
      builder: (context) => Positioned(
        width: MediaQuery.of(context).size.width - 32,
        child: CompositedTransformFollower(
          link: _layerLink,
          showWhenUnlinked: false,
          offset: const Offset(0, 56),
          child: Material(
            elevation: 4,
            borderRadius: BorderRadius.circular(8),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxHeight: 240),
              child: ListView.builder(
                padding: EdgeInsets.zero,
                shrinkWrap: true,
                itemCount: _suggestions.length,
                itemBuilder: (context, i) {
                  final s = _suggestions[i];
                  return ListTile(
                    dense: true,
                    title: Text(s.primary),
                    subtitle: s.secondary.isEmpty ? null : Text(s.secondary),
                    onTap: () => _pick(s),
                  );
                },
              ),
            ),
          ),
        ),
      ),
    );
    Overlay.of(context).insert(overlay);
    _overlay = overlay;
  }

  void _removeOverlay() {
    _overlay?.remove();
    _overlay = null;
  }

  @override
  Widget build(BuildContext context) {
    return CompositedTransformTarget(
      link: _layerLink,
      child: TextField(
        key: widget.testKey == null ? null : Key(widget.testKey!),
        controller: widget.controller,
        focusNode: _focusNode,
        decoration: InputDecoration(
          labelText: widget.label,
          hintText: widget.hintText,
          border: const OutlineInputBorder(),
        ),
        onChanged: _onChanged,
      ),
    );
  }
}
```

**Verify:** `cd mobile && flutter analyze` → "No issues found!" (or 0 errors).
**Commit:** `feat(scan-mobile): lease-prefill DTO, pdf stitch, Places repo+widget (frozen contracts)`

### Step A3 — Add a lease sample document

The committed `Docs/sample-scans/` has a `generate.py` that renders receipts/checks. Add a lease entry so the front-door flow has a checked-in sample.

**File:** create `Docs/sample-scans/13-lease-sunset-ridge.txt` (plain-text source the team can render; the PNG/PDF can be generated later by extending `generate.py`). Content — a realistic 1-page residential lease with every field the schema extracts:

```
RESIDENTIAL LEASE AGREEMENT

Lease Number: SR-2026-014
Property: Sunset Ridge Apartments
Premises: 482 Sunset Ridge Drive, Unit 12B, Columbus, OH 43215
Unit details: 2 bedrooms, 1.5 bathrooms, approx. 940 sq ft

Landlord: Sunset Ridge Holdings LLC
Tenant (Lessee): Daniel R. Fletcher

Term: This lease begins on 2026-07-01 and ends on 2027-06-30.
Monthly Rent: $1,575.00, due on the 1st day of each month.
Security Deposit: $1,575.00
Late Fee: $50.00 if rent is not received by the 5th.

Signed: ____________________   Date: ____________________
```

**File:** update `Docs/sample-scans/README.md` — append a row to the document table (find the table, add):

```md
| 13 | `13-lease-sunset-ridge.txt` | Residential lease (Property + Unit + Tenant + Lease) | Front-door "New rental from your lease" flow |
```

**Verify:** `ls Docs/sample-scans/13-lease-sunset-ridge.txt && grep -n "13-lease-sunset-ridge" Docs/sample-scans/README.md`
**Commit:** `docs(sample-scans): add a residential-lease sample for the scan front door`

---

## LANE B — Web (depends on Lane A through A1)

> **Frozen contracts consumed:** `lease-prefill.ts`, `stitch-pdf.ts` (Lane A1). Field-component prop shapes = Contract 2. Override JSON = Contract 3.
> **Every step below restates the AC it satisfies.** Verify gate for the whole lane: `cd web && npx svelte-check --threshold error` → 0 errors (run after each step's edits).

### Step B1 — `AutoFilledBadge.svelte` (the "from your lease" hint — AC-3)

**File:** create `web/src/lib/components/forms/AutoFilledBadge.svelte`:

```svelte
<!--
  AutoFilledBadge — a subtle "from your lease" chip shown next to a field that the scan pre-filled,
  so the user can SEE exactly what the AI provided vs. what they typed (AC-3 transparency). Render
  only when `show` is true. Confidence (0..1) optionally tunes the tone but never blocks anything.
-->
<script lang="ts">
	let { show = false, confidence }: { show?: boolean; confidence?: number } = $props();
	const pct = $derived(confidence == null ? null : Math.round(confidence <= 1 ? confidence * 100 : confidence));
</script>

{#if show}
	<span
		class="inline-flex items-center gap-1 rounded-full bg-accent/15 px-1.5 py-0.5 text-[10px] font-medium text-accent-foreground"
		data-testid="auto-filled-badge"
		title={pct != null ? `From your lease — ${pct}% sure` : 'From your lease'}
	>
		from your lease{#if pct != null && pct < 80}&nbsp;· {pct}%{/if}
	</span>
{/if}
```

**Verify:** `cd web && npx svelte-check --threshold error` → 0 errors.
**Commit:** `feat(scan-web): AutoFilledBadge "from your lease" transparency chip`

### Step B2 — Extract `PropertyFields.svelte` (the ONE property-form UI — AC-2)

Create `web/src/lib/components/forms/PropertyFields.svelte` carrying EXACTLY the field rows currently inlined in the property modal (`properties/+page.svelte`), including `AddressAutocomplete` and `StateSelect`. Prop shape = Contract 2.

```svelte
<!--
  PropertyFields — the single source-of-truth field group for a Property create/edit form. Consumed
  by BOTH the manual New-Property modal (properties/+page.svelte) AND the guided scan flow's Step 1.
  Owns ONLY the field rows + inline errors + the optional "from your lease" badge — NOT the dialog
  chrome or submit button (each caller wraps it). Address uses the shared Google-Places AddressAutocomplete.
-->
<script lang="ts">
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import AddressAutocomplete from '$lib/components/shared/AddressAutocomplete.svelte';
	import StateSelect from '$lib/components/shared/StateSelect.svelte';
	import AutoFilledBadge from './AutoFilledBadge.svelte';
	import { STEP_FIELD_TO_EXTRACTION } from '$lib/scan/lease-prefill';

	let {
		form = $bindable(),
		errors = {},
		autoFilled,
		confidence,
		testidPrefix = 'property'
	}: {
		form: { name: string; type: string; addressLine1: string; addressLine2: string; city: string; state: string; postalCode: string; ownerEntityId: string };
		errors?: Record<string, string>;
		autoFilled?: Set<string>;
		confidence?: Record<string, number>;
		testidPrefix?: string;
	} = $props();

	const PROPERTY_TYPES = ['SingleFamily', 'MultiFamily', 'Condo', 'Townhouse', 'Commercial', 'Other'];
	const filled = (key: string) => !!autoFilled?.has(key);
	const conf = (key: string) => confidence?.[STEP_FIELD_TO_EXTRACTION[key] ?? ''];
</script>

<div class="grid gap-3 md:grid-cols-2" data-testid={`${testidPrefix}-fields`}>
	<div class="md:col-span-2">
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">Name</span>
			<AutoFilledBadge show={filled('name')} confidence={conf('name')} />
		</div>
		<Input data-testid={`${testidPrefix}-name-input`} bind:value={form.name} placeholder="Property name" />
		{#if errors.name}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-name-error`}>{errors.name}</p>{/if}
	</div>

	<div class="md:col-span-2">
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">Address</span>
			<AutoFilledBadge show={filled('addressLine1')} confidence={conf('addressLine1')} />
		</div>
		<AddressAutocomplete
			testid={`${testidPrefix}-address-input`}
			bind:value={form.addressLine1}
			placeholder="Street address"
			onresolved={(a) => {
				if (a.city) form.city = a.city;
				if (a.state) form.state = a.state;
				if (a.zip) form.postalCode = a.zip;
			}}
		/>
		{#if errors.addressLine1}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-address-error`}>{errors.addressLine1}</p>{/if}
	</div>

	<div>
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">City</span>
			<AutoFilledBadge show={filled('city')} confidence={conf('city')} />
		</div>
		<Input data-testid={`${testidPrefix}-city-input`} bind:value={form.city} placeholder="City" />
		{#if errors.city}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-city-error`}>{errors.city}</p>{/if}
	</div>

	<div class="grid grid-cols-2 gap-2">
		<div>
			<div class="mb-1 flex items-center gap-2">
				<span class="text-xs font-medium text-muted-foreground">State</span>
				<AutoFilledBadge show={filled('state')} confidence={conf('state')} />
			</div>
			<StateSelect bind:value={form.state} testid={`${testidPrefix}-state-input`} />
			{#if errors.state}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-state-error`}>{errors.state}</p>{/if}
		</div>
		<div>
			<div class="mb-1 flex items-center gap-2">
				<span class="text-xs font-medium text-muted-foreground">ZIP</span>
				<AutoFilledBadge show={filled('postalCode')} confidence={conf('postalCode')} />
			</div>
			<Input data-testid={`${testidPrefix}-postal-input`} bind:value={form.postalCode} placeholder="ZIP" />
			{#if errors.postalCode}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-postal-error`}>{errors.postalCode}</p>{/if}
		</div>
	</div>

	<div class="md:col-span-2">
		<span class="mb-1 block text-xs font-medium text-muted-foreground">Type</span>
		<Select.Root type="single" bind:value={form.type}>
			<Select.Trigger class="w-full" data-testid={`${testidPrefix}-type-input`}>{form.type || 'Select type'}</Select.Trigger>
			<Select.Content>
				{#each PROPERTY_TYPES as t}
					<Select.Item value={t} label={t}>{t}</Select.Item>
				{/each}
			</Select.Content>
		</Select.Root>
	</div>
</div>
```

> **Before writing**, verify `StateSelect.svelte` exists and its prop names: `grep -n "let {" web/src/lib/components/shared/StateSelect.svelte`. If its bindable prop is not `value` or it has no `testid` prop, adapt the two `StateSelect` usages accordingly (and in any other step that uses it). If `StateSelect` does not exist, replace it with a plain `<Input bind:value={form.state} maxlength={2} />`.

**Now refactor the manual property modal** (`web/src/routes/(protected)/properties/+page.svelte`) to consume it. Replace the inlined name/address/city/state/zip/type field markup inside `<Dialog.Content>` (the block around `:308-378`) with:

```svelte
<PropertyFields bind:form errors={formErrors} />
```

Add the import near the other imports: `import PropertyFields from '$lib/components/forms/PropertyFields.svelte';`. The existing `form` `$state` object (`emptyProperty:77`) already has the exact keys the component expects (`name,type,addressLine1,addressLine2,city,state,postalCode,ownerEntityId`) — confirm and leave `submitProperty`/`savePropertyMutation` untouched. (The owner-entity selector, if present in the modal and NOT in `PropertyFields`, stays in the page outside the component.)

**Verify:** `cd web && npx svelte-check --threshold error` → 0 errors. Manual smoke (Lane B reviewer): open `/properties`, "New property", confirm the form still creates a property.
**Commit:** `refactor(web): extract PropertyFields, consume in the manual property modal`

### Step B3 — Extract `UnitFields.svelte` + refactor the unit modal (AC-2)

Create `web/src/lib/components/forms/UnitFields.svelte` (prop shape Contract 2), mirroring the unit modal rows (`properties/[id]/+page.svelte:586-623`):

```svelte
<script lang="ts">
	import { Input } from '$lib/components/ui/input';
	import AutoFilledBadge from './AutoFilledBadge.svelte';
	import { STEP_FIELD_TO_EXTRACTION } from '$lib/scan/lease-prefill';

	let {
		form = $bindable(),
		errors = {},
		autoFilled,
		confidence,
		testidPrefix = 'unit'
	}: {
		form: { unitNumber: string; bedrooms: string; bathrooms: string; marketRent: string };
		errors?: Record<string, string>;
		autoFilled?: Set<string>;
		confidence?: Record<string, number>;
		testidPrefix?: string;
	} = $props();

	const filled = (key: string) => !!autoFilled?.has(key);
	const conf = (key: string) => confidence?.[STEP_FIELD_TO_EXTRACTION[key] ?? ''];
</script>

<div class="grid gap-3" data-testid={`${testidPrefix}-fields`}>
	<div>
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">Unit number</span>
			<AutoFilledBadge show={filled('unitNumber')} confidence={conf('unitNumber')} />
		</div>
		<Input data-testid={`${testidPrefix}-number-input`} bind:value={form.unitNumber} placeholder="Unit number" />
		{#if errors.unitNumber}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-number-error`}>{errors.unitNumber}</p>{/if}
	</div>
	<div class="grid grid-cols-3 gap-2">
		<div>
			<div class="mb-1 flex items-center gap-2">
				<span class="text-xs font-medium text-muted-foreground">Beds</span>
				<AutoFilledBadge show={filled('bedrooms')} confidence={conf('bedrooms')} />
			</div>
			<Input data-testid={`${testidPrefix}-bedrooms-input`} bind:value={form.bedrooms} placeholder="Beds" />
			{#if errors.bedrooms}<p class="mt-1 text-xs text-destructive">{errors.bedrooms}</p>{/if}
		</div>
		<div>
			<div class="mb-1 flex items-center gap-2">
				<span class="text-xs font-medium text-muted-foreground">Baths</span>
				<AutoFilledBadge show={filled('bathrooms')} confidence={conf('bathrooms')} />
			</div>
			<Input data-testid={`${testidPrefix}-bathrooms-input`} bind:value={form.bathrooms} placeholder="Baths" />
			{#if errors.bathrooms}<p class="mt-1 text-xs text-destructive">{errors.bathrooms}</p>{/if}
		</div>
		<div>
			<span class="mb-1 block text-xs font-medium text-muted-foreground">Rent</span>
			<Input data-testid={`${testidPrefix}-rent-input`} bind:value={form.marketRent} placeholder="Rent" />
			{#if errors.marketRent}<p class="mt-1 text-xs text-destructive">{errors.marketRent}</p>{/if}
		</div>
	</div>
</div>
```

Refactor the unit modal in `properties/[id]/+page.svelte`: replace the `<div class="grid gap-3" data-testid="unit-form">…</div>` body (`:590-622`) with `<UnitFields bind:form={unitForm} errors={unitFormErrors} />`; add the import; leave `submitUnit`/mutations untouched.

**Verify:** `cd web && npx svelte-check --threshold error` → 0 errors.
**Commit:** `refactor(web): extract UnitFields, consume in the unit modal`

### Step B4 — Extract `TenantFields.svelte` + refactor the tenant modal (AC-2)

Create `web/src/lib/components/forms/TenantFields.svelte` (Contract 2), mirroring `tenants/+page.svelte:230-257`:

```svelte
<script lang="ts">
	import { Input } from '$lib/components/ui/input';
	import AutoFilledBadge from './AutoFilledBadge.svelte';

	let {
		form = $bindable(),
		errors = {},
		autoFilled,
		testidPrefix = 'tenant'
	}: {
		form: { firstName: string; lastName: string; email: string; phone: string; emergencyContact: string };
		errors?: Record<string, string>;
		autoFilled?: Set<string>;
		testidPrefix?: string;
	} = $props();
	const filled = (key: string) => !!autoFilled?.has(key);
</script>

<div class="grid gap-3 md:grid-cols-2" data-testid={`${testidPrefix}-fields`}>
	<div>
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">First name</span>
			<AutoFilledBadge show={filled('firstName')} />
		</div>
		<Input data-testid={`${testidPrefix}-first-name-input`} bind:value={form.firstName} placeholder="First name" />
		{#if errors.firstName}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-first-name-error`}>{errors.firstName}</p>{/if}
	</div>
	<div>
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">Last name</span>
			<AutoFilledBadge show={filled('lastName')} />
		</div>
		<Input data-testid={`${testidPrefix}-last-name-input`} bind:value={form.lastName} placeholder="Last name" />
		{#if errors.lastName}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-last-name-error`}>{errors.lastName}</p>{/if}
	</div>
	<div>
		<span class="mb-1 block text-xs font-medium text-muted-foreground">Email</span>
		<Input data-testid={`${testidPrefix}-email-input`} bind:value={form.email} placeholder="Email" />
		{#if errors.email}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-email-error`}>{errors.email}</p>{/if}
	</div>
	<div>
		<span class="mb-1 block text-xs font-medium text-muted-foreground">Phone</span>
		<Input data-testid={`${testidPrefix}-phone-input`} bind:value={form.phone} placeholder="Phone" />
	</div>
	<div class="md:col-span-2">
		<span class="mb-1 block text-xs font-medium text-muted-foreground">Emergency contact</span>
		<Input data-testid={`${testidPrefix}-emergency-input`} bind:value={form.emergencyContact} placeholder="Emergency contact" />
	</div>
</div>
```

Refactor the tenant modal: replace the `<div class="grid gap-3 md:grid-cols-2" data-testid="tenant-form">…</div>` body with `<TenantFields bind:form errors={formErrors} />`; add import; leave `submit`/mutation untouched.

**Verify:** `cd web && npx svelte-check --threshold error` → 0 errors.
**Commit:** `refactor(web): extract TenantFields, consume in the tenant modal`

### Step B5 — Extract `LeaseTermFields.svelte` + refactor the lease modal's TERM rows (AC-2)

The lease modal mixes pickers (property/unit/tenant) with term fields. The guided flow handles property/unit/tenant in prior steps, so `LeaseTermFields` carries ONLY the terms. Refactor the manual lease modal to use `LeaseTermFields` for its term rows while KEEPING its property/unit/tenant `Select` pickers in the page.

Create `web/src/lib/components/forms/LeaseTermFields.svelte` (Contract 2):

```svelte
<script lang="ts">
	import { Input } from '$lib/components/ui/input';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import * as Select from '$lib/components/ui/select';
	import AutoFilledBadge from './AutoFilledBadge.svelte';
	import { STEP_FIELD_TO_EXTRACTION } from '$lib/scan/lease-prefill';

	let {
		form = $bindable(),
		errors = {},
		autoFilled,
		confidence,
		statuses = ['Draft', 'Active', 'Expired', 'Terminated'],
		testidPrefix = 'lease'
	}: {
		form: { leaseNumber: string; startDate: string; endDate: string; monthlyRent: string; securityDeposit: string; lateFeeAmount: string; rentDueDay: string; status: string; notes: string };
		errors?: Record<string, string>;
		autoFilled?: Set<string>;
		confidence?: Record<string, number>;
		statuses?: string[];
		testidPrefix?: string;
	} = $props();

	const filled = (key: string) => !!autoFilled?.has(key);
	const conf = (key: string) => confidence?.[STEP_FIELD_TO_EXTRACTION[key] ?? ''];
</script>

<div class="grid gap-3 md:grid-cols-2" data-testid={`${testidPrefix}-term-fields`}>
	<div class="md:col-span-2">
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">Lease number</span>
			<AutoFilledBadge show={filled('leaseNumber')} confidence={conf('leaseNumber')} />
		</div>
		<Input data-testid={`${testidPrefix}-number-input`} bind:value={form.leaseNumber} placeholder="Lease number" />
		{#if errors.leaseNumber}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-number-error`}>{errors.leaseNumber}</p>{/if}
	</div>
	<div>
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">Start date</span>
			<AutoFilledBadge show={filled('startDate')} confidence={conf('startDate')} />
		</div>
		<DatePicker testid={`${testidPrefix}-start-input`} bind:value={form.startDate} placeholder="Start date" max={form.endDate || undefined} />
		{#if errors.startDate}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-start-error`}>{errors.startDate}</p>{/if}
	</div>
	<div>
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">End date</span>
			<AutoFilledBadge show={filled('endDate')} confidence={conf('endDate')} />
		</div>
		<DatePicker testid={`${testidPrefix}-end-input`} bind:value={form.endDate} placeholder="End date" min={form.startDate || undefined} />
		{#if errors.endDate}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-end-error`}>{errors.endDate}</p>{/if}
	</div>
	<div>
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">Monthly rent</span>
			<AutoFilledBadge show={filled('monthlyRent')} confidence={conf('monthlyRent')} />
		</div>
		<Input data-testid={`${testidPrefix}-rent-input`} bind:value={form.monthlyRent} placeholder="Monthly rent" />
		{#if errors.monthlyRent}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-rent-error`}>{errors.monthlyRent}</p>{/if}
	</div>
	<div>
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">Security deposit</span>
			<AutoFilledBadge show={filled('securityDeposit')} confidence={conf('securityDeposit')} />
		</div>
		<Input data-testid={`${testidPrefix}-deposit-input`} bind:value={form.securityDeposit} placeholder="Security deposit" />
		{#if errors.securityDeposit}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-deposit-error`}>{errors.securityDeposit}</p>{/if}
	</div>
	<div class="grid grid-cols-2 gap-2">
		<div>
			<div class="mb-1 flex items-center gap-2">
				<span class="text-xs font-medium text-muted-foreground">Late fee</span>
				<AutoFilledBadge show={filled('lateFeeAmount')} confidence={conf('lateFeeAmount')} />
			</div>
			<Input data-testid={`${testidPrefix}-late-fee-input`} bind:value={form.lateFeeAmount} placeholder="Late fee" />
		</div>
		<div>
			<div class="mb-1 flex items-center gap-2">
				<span class="text-xs font-medium text-muted-foreground">Due day</span>
				<AutoFilledBadge show={filled('rentDueDay')} confidence={conf('rentDueDay')} />
			</div>
			<Input data-testid={`${testidPrefix}-due-day-input`} bind:value={form.rentDueDay} placeholder="Due day" />
			{#if errors.rentDueDay}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-due-day-error`}>{errors.rentDueDay}</p>{/if}
		</div>
	</div>
	<div class="md:col-span-2">
		<span class="mb-1 block text-xs font-medium text-muted-foreground">Status</span>
		<Select.Root type="single" bind:value={form.status}>
			<Select.Trigger class="w-full" data-testid={`${testidPrefix}-status-input`}>{form.status || 'Select status'}</Select.Trigger>
			<Select.Content>
				{#each statuses as s}
					<Select.Item value={s} label={s}>{s}</Select.Item>
				{/each}
			</Select.Content>
		</Select.Root>
	</div>
</div>
```

> Verify `DatePicker.svelte`'s bindable prop is `value` and accepts `testid`/`min`/`max`: `grep -n "let {" web/src/lib/components/shared/DatePicker.svelte`. The lease modal already uses `<DatePicker testid=… bind:value=… min/max=…>` (`leases/+page.svelte:332-339`), so this matches; if not, align to its actual props.

Refactor the manual lease modal (`leases/+page.svelte`): replace the term-field portion of the grid (lease number, start, end, status, monthly rent, security deposit, late fee, due day — `:284-372` minus the property/unit/tenant `Select` blocks) with `<LeaseTermFields bind:form errors={formErrors} />`. The form `$state` (`empty:62-70`) already holds these keys **except** `notes` — add `notes: ''` to `empty` and to `openEdit`'s seed (`notes: l.notes ?? ''`) so the component's `form.notes` binding is satisfied; the page already sends the whole `result.data`, and `leaseSchema` has `notes: optionalText`. Keep the property/unit/tenant `Select` pickers in the page above the component. Leave `submit`/mutation untouched.

**Verify:** `cd web && npx svelte-check --threshold error` → 0 errors. Smoke: `/leases` → "New Lease" still creates.
**Commit:** `refactor(web): extract LeaseTermFields, consume in the lease modal`

### Step B6 — Make `FileDrop.svelte` accept multiple files (AC for multi-photo intake)

**File:** `web/src/lib/components/FileDrop.svelte`. Add an opt-in `multiple` mode without breaking existing single-file callers.

- Change the prop block (`:17`) to:

```svelte
	let {
		onselected,
		onselectedmany,
		multiple = false
	}: {
		onselected?: (file: File) => void;
		onselectedmany?: (files: File[]) => void;
		multiple?: boolean;
	} = $props();
```

- Add a helper and update the handlers:

```svelte
	function handleFiles(files: File[]) {
		if (files.length === 0) return;
		for (const f of files) {
			if (!ALLOWED_MIME_TYPES.includes(f.type)) {
				toast.warning(`File type "${f.type || 'unknown'}" may not be supported. Expected a PDF or image.`);
			}
		}
		selectedFile = files[0];
		if (multiple) onselectedmany?.(files);
		else onselected?.(files[0]);
	}
```

- In `onDrop`: replace the single-file pick with `const files = Array.from(e.dataTransfer?.files ?? []); handleFiles(files);`.
- In `onInputChange`: `const files = Array.from((e.target as HTMLInputElement).files ?? []); handleFiles(files);`.
- On the `<input>` (`:84-91`) add `{multiple}` (so `multiple={false}` is a no-op for existing callers) and keep `accept="application/pdf,image/*"`.
- Keep the old `handleFile(file)` name only if other callers reference it; otherwise replace its two call sites. Existing single-file callers (`onselected`) keep working because `multiple` defaults false.

**Verify:** `cd web && npx svelte-check --threshold error` → 0 errors. Confirm existing single-file FileDrop users (scan upload page, LeasePhotoPrefill, batch page) still type-check.
**Commit:** `feat(web): FileDrop optional multiple-file mode (onselectedmany)`

### Step B7 — The guided flow route `/scan/new-rental` (AC-1, AC-2, AC-3, AC-4)

**File:** create `web/src/routes/(protected)/scan/new-rental/+page.svelte`. This is the heart of Lane B. It:
1. Uploads (single PDF, or many photos stitched via `stitchImagesToPdf`) as a Lease draft, polls to `Reviewing`.
2. Builds `LeasePrefill` from the draft fields.
3. Walks Step 1 Property → 2 Unit → 3 Tenant → 4 Lease, each rendering the shared `*Fields` component pre-filled, with a visible "Step N of 4" progress header and Back/Next.
4. Property step includes the duplicate-guard (link-existing vs create-new) using the draft's `leaseProposal`.
5. Final Review screen lists everything; on confirm, emits Contract-3 override JSON to `scan.confirm` (ONE `ConfirmAsLeaseAsync`).

```svelte
<script lang="ts">
	import { createMutation, createQuery } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { toast } from 'svelte-sonner';
	import { scan } from '$lib/api/scan';
	import { properties } from '$lib/api/endpoints/properties';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { Button } from '$lib/components/ui/button';
	import * as Select from '$lib/components/ui/select';
	import { Loader2 } from '@lucide/svelte';
	import FileDrop from '$lib/components/FileDrop.svelte';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import PropertyFields from '$lib/components/forms/PropertyFields.svelte';
	import UnitFields from '$lib/components/forms/UnitFields.svelte';
	import TenantFields from '$lib/components/forms/TenantFields.svelte';
	import LeaseTermFields from '$lib/components/forms/LeaseTermFields.svelte';
	import { propertySchema, unitSchema, tenantSchema, leaseSchema, parseForm } from '$lib/schemas';
	import { toLeasePrefill, type PrefillConfidence } from '$lib/scan/lease-prefill';
	import { stitchImagesToPdf } from '$lib/scan/stitch-pdf';
	import { showError, apiErrorMessage } from '$lib/utils/toast';

	const portfolioId = getCurrentPortfolioId();

	// ----- phase: capture -> processing -> steps -> review -> done -----
	type Phase = 'capture' | 'processing' | 'steps' | 'done';
	let phase = $state<Phase>('capture');
	let draftId = $state<number | null>(null);
	let confidence = $state<PrefillConfidence>({});

	// Step machine. 0=Property 1=Unit 2=Tenant 3=Lease 4=Review.
	let step = $state(0);
	const STEP_LABELS = ['Property', 'Unit', 'Tenant', 'Lease'];
	const TOTAL = STEP_LABELS.length;

	// ----- the four step forms (string-bound, schema-validated) -----
	let propertyForm = $state({ name: '', type: 'MultiFamily', addressLine1: '', addressLine2: '', city: '', state: '', postalCode: '', ownerEntityId: '' });
	let unitForm = $state({ unitNumber: '', bedrooms: '', bathrooms: '', marketRent: '' });
	let tenantForm = $state({ firstName: '', lastName: '', email: '', phone: '', emergencyContact: '' });
	let leaseForm = $state({ leaseNumber: '', startDate: '', endDate: '', monthlyRent: '', securityDeposit: '', lateFeeAmount: '', rentDueDay: '1', status: 'Active', notes: '' });

	let propertyErrors = $state<Record<string, string>>({});
	let unitErrors = $state<Record<string, string>>({});
	let tenantErrors = $state<Record<string, string>>({});
	let leaseErrors = $state<Record<string, string>>({});

	// Which step-form fields were auto-filled (drives the "from your lease" badge).
	let autoFilled = $state<Set<string>>(new Set());

	// ----- Property duplicate-guard: link an existing in-portfolio property, or create new -----
	const CREATE = '__create__';
	let propertyChoice = $state<string>(CREATE); // a real id string => link; CREATE => create-new
	let unitChoice = $state<string>(CREATE);      // existing unit id, or CREATE
	const isCreatingProperty = $derived(propertyChoice === CREATE);

	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId],
		queryFn: () => properties.list(portfolioId, { take: 200 }),
		enabled: phase === 'steps'
	}));
	const unitsQuery = createQuery(() => ({
		queryKey: ['units-for-new-rental', propertyChoice],
		queryFn: () => properties.listUnits(Number(propertyChoice)),
		enabled: phase === 'steps' && propertyChoice !== CREATE && !!propertyChoice
	}));
	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId],
		queryFn: () => tenants.list(portfolioId, { take: 200 }),
		enabled: phase === 'steps'
	}));

	// ----- upload: one PDF passes straight through; many photos are stitched first -----
	const uploadOne = createMutation(() => ({
		mutationFn: (file: File) => scan.upload(file, 'Lease'),
		onSuccess: (res) => { draftId = res.draftId; phase = 'processing'; },
		onError: (err) => showError(apiErrorMessage(err, 'Could not read that file. Try a clearer photo or the PDF.'))
	}));

	async function onPdf(file: File) {
		uploadOne.mutate(file);
	}
	async function onPhotos(files: File[]) {
		try {
			const pdf = await stitchImagesToPdf(files);
			uploadOne.mutate(pdf);
		} catch (e) {
			showError('Could not combine those photos. Try fewer, clearer shots or a PDF.');
		}
	}

	// poll the draft until Reviewing, then seed the forms
	const draftQuery = createQuery(() => ({
		queryKey: ['new-rental-draft', draftId],
		enabled: draftId != null && phase === 'processing',
		queryFn: () => scan.get(draftId as number),
		refetchInterval: (q) => {
			const s = q.state.data?.status;
			return s === 'Pending' || s === 'Processing' ? 1500 : false;
		}
	}));

	let seeded = $state(false);
	$effect(() => {
		const d = draftQuery.data;
		if (!d || phase !== 'processing') return;
		if (d.status === 'Failed' || d.status === 'Rejected') {
			showError('We could not read that document. Try a clearer photo or the PDF.');
			phase = 'capture';
			draftId = null;
			return;
		}
		if (d.status !== 'Reviewing' || seeded) return;
		const { values, confidence: conf } = toLeasePrefill(d.fields);
		confidence = conf;
		// seed property
		propertyForm.name = values.propertyName;
		propertyForm.addressLine1 = values.propertyAddress;
		propertyForm.city = values.propertyCity;
		propertyForm.state = values.propertyState;
		propertyForm.postalCode = values.propertyPostalCode;
		// seed unit
		unitForm.unitNumber = values.unitNumber || '1';
		unitForm.bedrooms = values.unitBedrooms || '0';
		unitForm.bathrooms = values.unitBathrooms || '0';
		unitForm.marketRent = values.monthlyRent || '0';
		// seed tenant (split on last space)
		if (values.tenantName) {
			const parts = values.tenantName.trim().split(/\s+/);
			tenantForm.firstName = parts.length > 1 ? parts.slice(0, -1).join(' ') : parts[0];
			tenantForm.lastName = parts.length > 1 ? parts[parts.length - 1] : '';
		}
		// seed lease terms
		leaseForm.leaseNumber = values.leaseNumber;
		leaseForm.startDate = values.startDate;
		leaseForm.endDate = values.endDate;
		leaseForm.monthlyRent = values.monthlyRent;
		leaseForm.securityDeposit = values.securityDeposit;
		leaseForm.lateFeeAmount = values.lateFee;
		leaseForm.rentDueDay = values.rentDueDay || '1';
		// auto-fill badge set
		const af = new Set<string>();
		if (values.propertyName) af.add('name');
		if (values.propertyAddress) af.add('addressLine1');
		if (values.propertyCity) af.add('city');
		if (values.propertyState) af.add('state');
		if (values.propertyPostalCode) af.add('postalCode');
		if (values.unitNumber) af.add('unitNumber');
		if (values.unitBedrooms) af.add('bedrooms');
		if (values.unitBathrooms) af.add('bathrooms');
		if (values.tenantName) { af.add('firstName'); af.add('lastName'); }
		if (values.leaseNumber) af.add('leaseNumber');
		if (values.startDate) af.add('startDate');
		if (values.endDate) af.add('endDate');
		if (values.monthlyRent) af.add('monthlyRent');
		if (values.securityDeposit) af.add('securityDeposit');
		if (values.lateFee) af.add('lateFeeAmount');
		if (values.rentDueDay) af.add('rentDueDay');
		autoFilled = af;
		// duplicate-guard default from the proposal
		const prop = d.leaseProposal?.property;
		if (prop?.action === 'link' && prop.existingId != null) propertyChoice = String(prop.existingId);
		else propertyChoice = CREATE;
		seeded = true;
		phase = 'steps';
		step = 0;
	});

	// ----- step navigation with per-step validation (AC-1) -----
	function validateStep(i: number): boolean {
		if (i === 0) {
			if (isCreatingProperty) {
				const r = parseForm(propertySchema, propertyForm);
				propertyErrors = r.errors ?? {};
				return !r.errors;
			}
			propertyErrors = {};
			return !!propertyChoice; // linking an existing property: no field validation
		}
		if (i === 1) {
			if (!isCreatingProperty && unitChoice !== CREATE) { unitErrors = {}; return !!unitChoice; }
			const r = parseForm(unitSchema, unitForm);
			unitErrors = r.errors ?? {};
			return !r.errors;
		}
		if (i === 2) {
			const r = parseForm(tenantSchema, tenantForm);
			tenantErrors = r.errors ?? {};
			return !r.errors;
		}
		if (i === 3) {
			// validate lease terms minus the picker ids (resolved from prior steps at confirm)
			const probe = { ...leaseForm, propertyId: '1', unitId: '1', tenantId: '1', moveInDate: '' };
			const r = parseForm(leaseSchema, probe);
			// drop id errors (they aren't user-entered here)
			const e = { ...(r.errors ?? {}) };
			delete e.propertyId; delete e.unitId; delete e.tenantId;
			leaseErrors = e;
			return Object.keys(e).length === 0;
		}
		return true;
	}

	function next() {
		if (!validateStep(step)) return;
		step = Math.min(step + 1, TOTAL); // TOTAL == review index
	}
	function back() { step = Math.max(step - 1, 0); }

	const selectedExistingPropertyLabel = $derived.by(() => {
		if (propertyChoice === CREATE) return 'Create new from the lease';
		return propertiesQuery.data?.find((p) => String(p.id) === propertyChoice)?.name ?? 'Select a property';
	});
	const selectedExistingUnitLabel = $derived.by(() => {
		if (unitChoice === CREATE) return 'Create new from the lease';
		return unitsQuery.data?.find((u) => String(u.id) === unitChoice)
			? `Unit ${unitsQuery.data?.find((u) => String(u.id) === unitChoice)?.unitNumber}`
			: 'Select a unit';
	});

	// ----- confirm: emit Contract-3 override JSON, ONE ConfirmAsLeaseAsync -----
	function buildOverrides(): string {
		const o: Record<string, unknown> = {};
		if (isCreatingProperty) {
			o.propertyId = null;
			if (propertyForm.name.trim()) o.propertyName = propertyForm.name.trim();
			if (propertyForm.addressLine1.trim()) o.propertyAddress = propertyForm.addressLine1.trim();
			if (propertyForm.city.trim()) o.propertyCity = propertyForm.city.trim();
			if (propertyForm.state.trim()) o.propertyState = propertyForm.state.trim();
			if (propertyForm.postalCode.trim()) o.propertyPostalCode = propertyForm.postalCode.trim();
		} else {
			o.propertyId = Number(propertyChoice);
		}
		if (!isCreatingProperty && unitChoice !== CREATE) {
			o.unitId = Number(unitChoice);
		} else {
			o.unitId = null;
			if (unitForm.unitNumber.trim()) o.unitNumber = unitForm.unitNumber.trim();
			if (unitForm.bedrooms.trim()) o.unitBedrooms = Number(unitForm.bedrooms);
			if (unitForm.bathrooms.trim()) o.unitBathrooms = Number(unitForm.bathrooms);
		}
		// tenant: always create/match by name from the tenant step (no tenant linking in this flow yet)
		const fullName = `${tenantForm.firstName} ${tenantForm.lastName}`.trim();
		if (fullName) o.tenantName = fullName;
		// lease terms
		o.leaseNumber = leaseForm.leaseNumber.trim();
		o.startDate = leaseForm.startDate;
		o.endDate = leaseForm.endDate;
		if (leaseForm.monthlyRent.trim()) o.monthlyRent = Number(leaseForm.monthlyRent);
		if (leaseForm.securityDeposit.trim()) o.securityDeposit = Number(leaseForm.securityDeposit);
		if (leaseForm.lateFeeAmount.trim()) o.lateFee = Number(leaseForm.lateFeeAmount);
		if (leaseForm.rentDueDay.trim()) o.rentDueDay = Number(leaseForm.rentDueDay);
		return JSON.stringify(o);
	}

	const confirmMutation = createMutation(() => ({
		mutationFn: () => scan.confirm(draftId as number, buildOverrides()),
		onSuccess: (res) => {
			toast.success('Your rental is set up.');
			phase = 'done';
			if (res.leaseId) goto(`/leases/${res.leaseId}`);
			else goto('/leases');
		},
		onError: (err) => showError(apiErrorMessage(err, 'Could not create the rental. Check the details and try again.'))
	}));
</script>

<div class="mx-auto max-w-2xl space-y-4 p-4">
	<PageBreadcrumb segments={[{ label: 'Scan', href: '/scan' }, { label: 'New rental from your lease' }]} />

	{#if phase === 'capture'}
		<div class="space-y-4" data-testid="new-rental-capture">
			<div>
				<h1 class="text-lg font-semibold text-foreground">New rental from your lease</h1>
				<p class="text-sm text-muted-foreground">Snap the lease (multiple photos are fine) or upload the PDF. We'll read it and pre-fill the property, unit, tenant, and lease for you to review.</p>
			</div>
			<div>
				<p class="mb-1 text-xs font-medium text-muted-foreground">Photos of the lease (one or many)</p>
				<FileDrop multiple onselectedmany={onPhotos} />
			</div>
			<div>
				<p class="mb-1 text-xs font-medium text-muted-foreground">…or a single PDF</p>
				<FileDrop onselected={onPdf} />
			</div>
		</div>
	{:else if phase === 'processing'}
		<div class="flex items-center gap-2 py-10 text-sm text-muted-foreground" data-testid="new-rental-processing">
			<Loader2 class="h-4 w-4 animate-spin" /> Reading your lease… this takes a few seconds.
		</div>
	{:else if phase === 'steps'}
		<!-- AC-1: visible step progress -->
		<div data-testid="new-rental-stepper">
			<div class="mb-1 flex items-center justify-between text-xs text-muted-foreground">
				<span data-testid="new-rental-step-label">
					{step < TOTAL ? `Step ${step + 1} of ${TOTAL} · ${STEP_LABELS[step]}` : 'Review & confirm'}
				</span>
				<span>{Math.min(step + 1, TOTAL + 1)}/{TOTAL + 1}</span>
			</div>
			<div class="h-1.5 w-full overflow-hidden rounded-full bg-muted">
				<div class="h-full bg-primary transition-all" style={`width:${((Math.min(step, TOTAL) + 1) / (TOTAL + 1)) * 100}%`}></div>
			</div>
		</div>

		{#if step === 0}
			<div class="space-y-3" data-testid="new-rental-step-property">
				<h2 class="text-base font-semibold text-foreground">Property</h2>
				<!-- AC-4 duplicate-guard at the Property step -->
				<div>
					<p class="mb-1 text-xs font-medium text-muted-foreground">Is this one of your existing properties?</p>
					<Select.Root type="single" bind:value={propertyChoice}>
						<Select.Trigger class="w-full" data-testid="new-rental-property-choice">{selectedExistingPropertyLabel}</Select.Trigger>
						<Select.Content>
							<Select.Item value={CREATE} label="Create new from the lease">Create new from the lease</Select.Item>
							{#each propertiesQuery.data ?? [] as p (p.id)}
								<Select.Item value={String(p.id)} label={p.name}>{p.name}</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
					{#if propertiesQuery.data && propertiesQuery.data.length > 0 && isCreatingProperty}
						<p class="mt-1 text-xs text-[var(--warning)]" data-testid="new-rental-dupe-hint">If this lease is for a property you already have, pick it above to avoid a duplicate.</p>
					{/if}
				</div>
				{#if isCreatingProperty}
					<PropertyFields bind:form={propertyForm} errors={propertyErrors} {autoFilled} {confidence} testidPrefix="new-rental-property" />
				{/if}
			</div>
		{:else if step === 1}
			<div class="space-y-3" data-testid="new-rental-step-unit">
				<h2 class="text-base font-semibold text-foreground">Unit</h2>
				{#if !isCreatingProperty}
					<div>
						<p class="mb-1 text-xs font-medium text-muted-foreground">Pick an existing unit, or create one.</p>
						<Select.Root type="single" bind:value={unitChoice}>
							<Select.Trigger class="w-full" data-testid="new-rental-unit-choice">{selectedExistingUnitLabel}</Select.Trigger>
							<Select.Content>
								<Select.Item value={CREATE} label="Create new from the lease">Create new from the lease</Select.Item>
								{#each unitsQuery.data ?? [] as u (u.id)}
									<Select.Item value={String(u.id)} label={`Unit ${u.unitNumber}`}>Unit {u.unitNumber} ({u.status})</Select.Item>
								{/each}
							</Select.Content>
						</Select.Root>
					</div>
				{/if}
				{#if isCreatingProperty || unitChoice === CREATE}
					<UnitFields bind:form={unitForm} errors={unitErrors} {autoFilled} {confidence} testidPrefix="new-rental-unit" />
				{/if}
			</div>
		{:else if step === 2}
			<div class="space-y-3" data-testid="new-rental-step-tenant">
				<h2 class="text-base font-semibold text-foreground">Tenant</h2>
				<TenantFields bind:form={tenantForm} errors={tenantErrors} {autoFilled} testidPrefix="new-rental-tenant" />
			</div>
		{:else if step === 3}
			<div class="space-y-3" data-testid="new-rental-step-lease">
				<h2 class="text-base font-semibold text-foreground">Lease</h2>
				<LeaseTermFields bind:form={leaseForm} errors={leaseErrors} {autoFilled} {confidence} testidPrefix="new-rental-lease" />
			</div>
		{:else}
			<!-- AC-4: final review before save -->
			<div class="space-y-3" data-testid="new-rental-review">
				<h2 class="text-base font-semibold text-foreground">Here's what I'll add</h2>
				<ul class="space-y-2 rounded-md border border-border bg-muted/30 p-3 text-sm">
					<li data-testid="review-property"><span class="font-medium">Property:</span>
						{#if isCreatingProperty}{propertyForm.name || propertyForm.addressLine1} — {[propertyForm.addressLine1, propertyForm.city, propertyForm.state].filter(Boolean).join(', ')} <span class="text-xs text-[var(--success)]">(new)</span>
						{:else}{selectedExistingPropertyLabel} <span class="text-xs text-muted-foreground">(existing)</span>{/if}
					</li>
					<li data-testid="review-unit"><span class="font-medium">Unit:</span>
						{#if !isCreatingProperty && unitChoice !== CREATE}{selectedExistingUnitLabel} <span class="text-xs text-muted-foreground">(existing)</span>
						{:else}Unit {unitForm.unitNumber} <span class="text-xs text-[var(--success)]">(new)</span>{/if}
					</li>
					<li data-testid="review-tenant"><span class="font-medium">Tenant:</span> {`${tenantForm.firstName} ${tenantForm.lastName}`.trim() || '—'}</li>
					<li data-testid="review-lease"><span class="font-medium">Lease:</span> ${leaseForm.monthlyRent}/mo, {leaseForm.startDate} – {leaseForm.endDate}</li>
				</ul>
				<p class="text-xs text-muted-foreground">Nothing is saved until you tap Confirm. We'll create everything in one step.</p>
			</div>
		{/if}

		<div class="flex items-center justify-between pt-2">
			<Button variant="ghost" onclick={back} disabled={step === 0} data-testid="new-rental-back">Back</Button>
			{#if step < TOTAL}
				<Button onclick={next} data-testid="new-rental-next">Next</Button>
			{:else}
				<Button onclick={() => confirmMutation.mutate()} disabled={confirmMutation.isPending} data-testid="new-rental-confirm">
					{confirmMutation.isPending ? 'Creating…' : 'Confirm & create'}
				</Button>
			{/if}
		</div>
	{/if}
</div>
```

> **Adapt to real component props.** Before finalizing, verify these imports/props resolve in THIS repo (grep each): `getCurrentPortfolioId` from `$lib/stores/portfolio.svelte` (the existing scan review page imports it `[draftId]/+page.svelte:10`); `showError`/`apiErrorMessage` from `$lib/utils/toast` (used in `LeasePhotoPrefill.svelte:7`); `PageBreadcrumb` prop name (`grep -n "let {" web/src/lib/components/shared/PageBreadcrumb.svelte` — adjust `segments` if its prop differs). `properties.listUnits` / `properties.list` / `tenants.list` signatures are confirmed (`endpoints/properties.ts`, `endpoints/tenants.ts`). If `Select.Item value=""` semantics bite (bits-ui treats `''` as "nothing selected"), the `CREATE='__create__'` sentinel already avoids that.

**Verify:** `cd web && npx svelte-check --threshold error` → 0 errors.
**Commit:** `feat(web): guided "New rental from your lease" stepped flow (/scan/new-rental)`

### Step B8 — Entry points: route into the guided flow (AC-2 reuse + discoverability)

- **File:** `web/src/routes/(protected)/scan/+page.svelte` — add a primary CTA card linking to `/scan/new-rental`. Open it (`Read`), find the existing intake CTAs, and add (matching the page's existing card style):

```svelte
<a href="/scan/new-rental" class="block rounded-lg border border-accent/40 bg-accent/5 p-4 hover:bg-accent/10" data-testid="scan-new-rental-cta">
	<p class="text-sm font-semibold text-foreground">New rental from your lease</p>
	<p class="text-xs text-muted-foreground">Snap or upload a lease → we pre-fill the property, unit, tenant, and lease for you to review.</p>
</a>
```

- **File:** `web/src/lib/components/onboarding/LeasePhotoPrefill.svelte` — this onboarding helper only captures 7 scalar terms and drops property/unit/tenant. Add a secondary action that routes the user into the full guided flow for the complete experience, WITHOUT removing the existing inline prefill (some onboarding contexts want just the terms). After the existing "Snap a photo" button (`:127-130`), add:

```svelte
<a href="/scan/new-rental" class="text-xs text-muted-foreground underline-offset-4 hover:underline" data-testid="lease-prefill-full-flow">
	or set up the whole rental from the lease →
</a>
```

> Generalizing `LeasePhotoPrefill` itself to carry property/unit/tenant (gap #3) is satisfied by routing to `/scan/new-rental`, which IS the generalized surface. Do not duplicate the stepper inside the onboarding component.

**Verify:** `cd web && npx svelte-check --threshold error` → 0 errors.
**Commit:** `feat(web): surface the guided lease flow from the scan hub and onboarding`

---

## LANE C — Mobile (depends on Lane A through A2)

> **Frozen contracts consumed:** `lease_prefill.dart`, `pdf_stitch.dart`, `places_repository.dart`, `address_autocomplete_field.dart` (Lane A2). Override map = the mobile analog of Contract 3 (already built by `buildOverridesMap`).
> **Verify gate:** `cd mobile && flutter analyze` → 0 errors after each step. **Do not run `flutter analyze` at the same instant as a web `svelte-check` — serialize.**

### Step C1 — Multi-image capture + client stitch (multi-photo intake; AC source for mobile)

**File:** `mobile/lib/features/capture/capture_fab_sheet.dart`. Replace single-image capture with multi-image for the lease path, stitch to PDF, upload as Lease.

- Add imports at the top:

```dart
import 'pdf_stitch_import'; // placeholder — see exact import below
```

Use the real import lines:

```dart
import '../scan/pdf_stitch.dart';
```

- Add a new "Scan a lease" flow. Locate the existing options list (`:185-238`) and ADD a dedicated lease option above "Scan" (keep all existing options intact). The new handler picks MANY images, stitches, and uploads as Lease:

```dart
Future<void> _scanLease() async {
  final picker = ImagePicker();
  final picked = await picker.pickMultiImage(imageQuality: 80, maxWidth: 1600, maxHeight: 1600);
  if (picked.isEmpty) return;
  setState(() => _busy = true); // reuse whatever busy flag the sheet already has; if none, add `bool _busy=false;`
  try {
    final images = <Uint8List>[];
    for (final x in picked) {
      images.add(Uint8List.fromList(await x.readAsBytes()));
    }
    final pdf = await stitchImagesToPdf(images);
    final created = await ref
        .read(scanRepositoryProvider)
        .uploadImage(pdf, 'lease-scan.pdf', 'application/pdf', targetEntityType: 'Lease');
    if (!mounted) return;
    Navigator.of(context).pop();
    // Route into the guided flow (Step C2). guidedRentalRoute is the new screen's route name/builder.
    GuidedRentalFlow.open(context, created.draftId);
  } catch (e) {
    if (mounted) setState(() => _error = "Couldn't read that lease. Try clearer photos or a PDF.");
  } finally {
    if (mounted) setState(() => _busy = false);
  }
}
```

> Verify `pickMultiImage` is available on `image_picker:^1.1.2` (it is). Verify the sheet's existing state field names for busy/error (`grep -n "_busy\|_error\|setState" mobile/lib/features/capture/capture_fab_sheet.dart`) and reuse them; add `bool _busy = false;` only if absent. Verify `_uploadAndReview` is where existing single uploads route — the lease option should route into `GuidedRentalFlow` (C2), NOT the legacy `scan_review_screen`.
- Add the matching option tile in the options list (match the existing tile widget pattern, e.g. `_CaptureOption(icon: Icons.home_work_outlined, label: 'Scan a lease', onTap: _scanLease)`).
- Update the PDF-single path comment (`:99-100`) to note multi-photo lease assembly now exists: change the "is P1" comment to "multi-photo lease assembly shipped via `_scanLease` (pdf_stitch.dart)."

**Verify:** `cd mobile && flutter analyze` → 0 errors.
**Commit:** `feat(scan-mobile): multi-photo lease capture stitched to one PDF`

### Step C2 — The guided rental flow screen (AC-1, AC-2, AC-3, AC-4, AC-5)

**File:** create `mobile/lib/features/scan/guided_rental_flow.dart`. A stepped screen mirroring web Lane B: polls the Lease draft to Reviewing, builds `LeasePrefill`, walks Property → Unit → Tenant → Lease → Review, reuses the add-sheet field groups, uses the Places widget for the address, and confirms once.

```dart
import 'dart:convert';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'lease_prefill.dart';
import 'scan_repository.dart';
import '../places/address_autocomplete_field.dart';
import '../places/places_repository.dart';

/// Guided, explicitly-stepped, PRE-FILLED "New rental from your lease" flow (mobile mirror of the
/// web /scan/new-rental). One entity per step with a visible "Step N of 4" indicator (AC-1/AC-5),
/// reuses the real create fields pre-filled (AC-2), shows a "from your lease" badge on auto-filled
/// fields (AC-3), and ends on a review screen that creates everything in ONE confirm (AC-4).
class GuidedRentalFlow extends ConsumerStatefulWidget {
  const GuidedRentalFlow({super.key, required this.draftId});
  final int draftId;

  static Future<void> open(BuildContext context, int draftId) {
    return Navigator.of(context).push(
      MaterialPageRoute(builder: (_) => GuidedRentalFlow(draftId: draftId)),
    );
  }

  @override
  ConsumerState<GuidedRentalFlow> createState() => _GuidedRentalFlowState();
}

class _GuidedRentalFlowState extends ConsumerState<GuidedRentalFlow> {
  static const _labels = ['Property', 'Unit', 'Tenant', 'Lease'];
  static const _total = 4;

  int _step = 0;
  bool _loading = true;
  String? _error;
  bool _confirming = false;
  Set<String> _filled = {};

  // Property
  final _propName = TextEditingController();
  final _propAddress = TextEditingController();
  final _propCity = TextEditingController();
  final _propState = TextEditingController();
  final _propZip = TextEditingController();
  // Unit
  final _unitNumber = TextEditingController();
  final _unitBeds = TextEditingController();
  final _unitBaths = TextEditingController();
  // Tenant
  final _tenantFirst = TextEditingController();
  final _tenantLast = TextEditingController();
  // Lease
  final _leaseNumber = TextEditingController();
  final _rent = TextEditingController();
  final _deposit = TextEditingController();
  final _lateFee = TextEditingController();
  final _dueDay = TextEditingController(text: '1');
  DateTime? _start;
  DateTime? _end;

  @override
  void initState() {
    super.initState();
    _poll();
  }

  Future<void> _poll() async {
    final repo = ref.read(scanRepositoryProvider);
    for (var i = 0; i < 40; i++) {
      final draft = await repo.getDraft(widget.draftId); // see C-note: add getDraft to repo if absent
      final status = (draft['status'] ?? '').toString();
      if (status == 'Reviewing') {
        _seed((draft['fields'] as List<dynamic>? ?? const [])
            .whereType<Map<String, dynamic>>()
            .toList());
        return;
      }
      if (status == 'Failed' || status == 'Rejected') {
        setState(() {
          _loading = false;
          _error = "We couldn't read that lease. Go back and try clearer photos or a PDF.";
        });
        return;
      }
      await Future<void>.delayed(const Duration(milliseconds: 1500));
    }
    setState(() {
      _loading = false;
      _error = 'Reading the lease took too long. Please try again.';
    });
  }

  void _seed(List<Map<String, dynamic>> fields) {
    final r = LeasePrefill.fromFields(fields);
    final v = r.values;
    _filled = r.filled;
    _propName.text = v.propertyName;
    _propAddress.text = v.propertyAddress;
    _propCity.text = v.propertyCity;
    _propState.text = v.propertyState;
    _propZip.text = v.propertyPostalCode;
    _unitNumber.text = v.unitNumber.isEmpty ? '1' : v.unitNumber;
    _unitBeds.text = v.unitBedrooms;
    _unitBaths.text = v.unitBathrooms;
    if (v.tenantName.isNotEmpty) {
      final parts = v.tenantName.trim().split(RegExp(r'\s+'));
      _tenantFirst.text = parts.length > 1 ? parts.sublist(0, parts.length - 1).join(' ') : parts.first;
      _tenantLast.text = parts.length > 1 ? parts.last : '';
    }
    _leaseNumber.text = v.leaseNumber;
    _rent.text = v.monthlyRent;
    _deposit.text = v.securityDeposit;
    _lateFee.text = v.lateFee;
    _dueDay.text = v.rentDueDay.isEmpty ? '1' : v.rentDueDay;
    _start = DateTime.tryParse(v.startDate);
    _end = DateTime.tryParse(v.endDate);
    setState(() => _loading = false);
  }

  bool _badge(String field) => _filled.contains(field);

  Widget _fromLeaseBadge() => Container(
        padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
        decoration: BoxDecoration(
          color: Theme.of(context).colorScheme.secondaryContainer,
          borderRadius: BorderRadius.circular(10),
        ),
        child: Text('from your lease', style: Theme.of(context).textTheme.labelSmall),
      );

  Widget _field(String label, TextEditingController c, {String? extractionKey, TextInputType? keyboard}) {
    final filled = extractionKey != null && _badge(extractionKey);
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(children: [
            Text(label, style: Theme.of(context).textTheme.bodySmall),
            if (filled) ...[const SizedBox(width: 6), _fromLeaseBadge()],
          ]),
          const SizedBox(height: 4),
          TextField(
            controller: c,
            keyboardType: keyboard,
            decoration: const InputDecoration(border: OutlineInputBorder()),
          ),
        ],
      ),
    );
  }

  String _overridesJson() {
    final o = <String, dynamic>{};
    // This flow always creates from the document (mobile v1: no existing-link picker — parity follow-up).
    o['propertyId'] = null;
    if (_propName.text.trim().isNotEmpty) o['propertyName'] = _propName.text.trim();
    if (_propAddress.text.trim().isNotEmpty) o['propertyAddress'] = _propAddress.text.trim();
    if (_propCity.text.trim().isNotEmpty) o['propertyCity'] = _propCity.text.trim();
    if (_propState.text.trim().isNotEmpty) o['propertyState'] = _propState.text.trim();
    if (_propZip.text.trim().isNotEmpty) o['propertyPostalCode'] = _propZip.text.trim();
    o['unitId'] = null;
    if (_unitNumber.text.trim().isNotEmpty) o['unitNumber'] = _unitNumber.text.trim();
    if (_unitBeds.text.trim().isNotEmpty) o['unitBedrooms'] = num.tryParse(_unitBeds.text.trim());
    if (_unitBaths.text.trim().isNotEmpty) o['unitBathrooms'] = num.tryParse(_unitBaths.text.trim());
    final name = '${_tenantFirst.text.trim()} ${_tenantLast.text.trim()}'.trim();
    if (name.isNotEmpty) o['tenantName'] = name;
    o['leaseNumber'] = _leaseNumber.text.trim();
    if (_start != null) o['startDate'] = _iso(_start!);
    if (_end != null) o['endDate'] = _iso(_end!);
    if (_rent.text.trim().isNotEmpty) o['monthlyRent'] = num.tryParse(_rent.text.trim());
    if (_deposit.text.trim().isNotEmpty) o['securityDeposit'] = num.tryParse(_deposit.text.trim());
    if (_lateFee.text.trim().isNotEmpty) o['lateFee'] = num.tryParse(_lateFee.text.trim());
    if (_dueDay.text.trim().isNotEmpty) o['rentDueDay'] = int.tryParse(_dueDay.text.trim());
    return jsonEncode(o);
  }

  String _iso(DateTime d) => '${d.year.toString().padLeft(4, '0')}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';

  bool _validateStep() {
    String? err;
    if (_step == 0) {
      if (_propName.text.trim().isEmpty && _propAddress.text.trim().isEmpty) err = 'Enter a property name or address.';
    } else if (_step == 1) {
      if (_unitNumber.text.trim().isEmpty) err = 'Enter a unit number.';
    } else if (_step == 2) {
      if (_tenantFirst.text.trim().isEmpty) err = 'Enter the tenant\'s first name.';
    } else if (_step == 3) {
      if (_start == null || _end == null) err = 'Pick the lease start and end dates.';
      else if ((num.tryParse(_rent.text.trim()) ?? 0) <= 0) err = 'Enter the monthly rent.';
    }
    if (err != null) {
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(err)));
      return false;
    }
    return true;
  }

  Future<void> _confirm() async {
    setState(() => _confirming = true);
    try {
      final res = await ref.read(scanRepositoryProvider).confirm(widget.draftId, jsonDecode(_overridesJson()) as Map<String, dynamic>);
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Your rental is set up.')));
      Navigator.of(context).pop(res?['leaseId']);
    } catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Could not create the rental. Check the details and try again.')));
    } finally {
      if (mounted) setState(() => _confirming = false);
    }
  }

  Future<void> _pickDate(bool start) async {
    final picked = await showDatePicker(
      context: context,
      initialDate: (start ? _start : _end) ?? DateTime.now(),
      firstDate: DateTime(2000),
      lastDate: DateTime(2100),
    );
    if (picked != null) setState(() => start ? _start = picked : _end = picked);
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: Text(_step < _total ? 'Step ${_step + 1} of $_total · ${_labels[_step]}' : 'Review & confirm'),
      ),
      body: _loading
          ? const Center(child: Padding(padding: EdgeInsets.all(24), child: Column(mainAxisSize: MainAxisSize.min, children: [CircularProgressIndicator(), SizedBox(height: 12), Text('Reading your lease…')])))
          : _error != null
              ? Center(child: Padding(padding: const EdgeInsets.all(24), child: Text(_error!)))
              : Column(
                  children: [
                    LinearProgressIndicator(value: (_step + 1) / (_total + 1)),
                    Expanded(child: SingleChildScrollView(padding: const EdgeInsets.all(16), child: _stepBody())),
                    SafeArea(
                      child: Padding(
                        padding: const EdgeInsets.all(12),
                        child: Row(
                          children: [
                            TextButton(
                              onPressed: _step == 0 ? null : () => setState(() => _step -= 1),
                              child: const Text('Back'),
                            ),
                            const Spacer(),
                            if (_step < _total)
                              FilledButton(
                                onPressed: () { if (_validateStep()) setState(() => _step += 1); },
                                child: const Text('Next'),
                              )
                            else
                              FilledButton(
                                onPressed: _confirming ? null : _confirm,
                                child: Text(_confirming ? 'Creating…' : 'Confirm & create'),
                              ),
                          ],
                        ),
                      ),
                    ),
                  ],
                ),
    );
  }

  Widget _stepBody() {
    switch (_step) {
      case 0:
        return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          _field('Property name', _propName, extractionKey: 'property_name'),
          // Places-backed street address (AC: Google Places on mobile via existing proxy)
          Row(children: [
            Text('Address', style: Theme.of(context).textTheme.bodySmall),
            if (_badge('property_address')) ...[const SizedBox(width: 6), _fromLeaseBadge()],
          ]),
          const SizedBox(height: 4),
          AddressAutocompleteField(
            controller: _propAddress,
            onResolved: (a) {
              if (a.city.isNotEmpty) _propCity.text = a.city;
              if (a.state.isNotEmpty) _propState.text = a.state;
              if (a.zip.isNotEmpty) _propZip.text = a.zip;
            },
          ),
          const SizedBox(height: 12),
          _field('City', _propCity, extractionKey: 'property_city'),
          _field('State', _propState, extractionKey: 'property_state'),
          _field('ZIP', _propZip, extractionKey: 'property_postal_code'),
        ]);
      case 1:
        return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          _field('Unit number', _unitNumber, extractionKey: 'unit_number'),
          _field('Bedrooms', _unitBeds, extractionKey: 'unit_bedrooms', keyboard: TextInputType.number),
          _field('Bathrooms', _unitBaths, extractionKey: 'unit_bathrooms', keyboard: TextInputType.number),
        ]);
      case 2:
        return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          _field('First name', _tenantFirst, extractionKey: 'tenant_name'),
          _field('Last name', _tenantLast, extractionKey: 'tenant_name'),
        ]);
      case 3:
        return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          _field('Lease number', _leaseNumber, extractionKey: 'lease_number'),
          ListTile(
            contentPadding: EdgeInsets.zero,
            title: const Text('Start date'),
            subtitle: Text(_start == null ? 'Pick a date' : _iso(_start!)),
            trailing: const Icon(Icons.calendar_today_outlined),
            onTap: () => _pickDate(true),
          ),
          ListTile(
            contentPadding: EdgeInsets.zero,
            title: const Text('End date'),
            subtitle: Text(_end == null ? 'Pick a date' : _iso(_end!)),
            trailing: const Icon(Icons.calendar_today_outlined),
            onTap: () => _pickDate(false),
          ),
          _field('Monthly rent', _rent, extractionKey: 'monthly_rent', keyboard: TextInputType.number),
          _field('Security deposit', _deposit, extractionKey: 'security_deposit', keyboard: TextInputType.number),
          _field('Late fee', _lateFee, extractionKey: 'late_fee', keyboard: TextInputType.number),
          _field('Rent due day', _dueDay, extractionKey: 'rent_due_day', keyboard: TextInputType.number),
        ]);
      default:
        // AC-4: review before save
        return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          const Text("Here's what I'll add", style: TextStyle(fontWeight: FontWeight.w600, fontSize: 16)),
          const SizedBox(height: 8),
          Card(child: Padding(padding: const EdgeInsets.all(12), child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text('Property: ${_propName.text.isNotEmpty ? _propName.text : _propAddress.text} — ${[_propAddress.text, _propCity.text, _propState.text].where((s) => s.isNotEmpty).join(', ')}'),
            const SizedBox(height: 4),
            Text('Unit: ${_unitNumber.text}'),
            const SizedBox(height: 4),
            Text('Tenant: ${'${_tenantFirst.text} ${_tenantLast.text}'.trim()}'),
            const SizedBox(height: 4),
            Text('Lease: \$${_rent.text}/mo, ${_start != null ? _iso(_start!) : '—'} – ${_end != null ? _iso(_end!) : '—'}'),
          ]))),
          const SizedBox(height: 8),
          Text('Nothing is saved until you tap Confirm.', style: Theme.of(context).textTheme.bodySmall),
        ]);
    }
  }
}
```

> **C-note (repository method):** the poll uses `repo.getDraft(int id)` returning the raw draft JSON map. The existing `ScanRepository` (`scan_repository.dart`) has `confirm` + `uploadImage`; verify whether a draft GET exists (`grep -n "getDraft\|/scans/\$id\b\|getScan\|fetchDraft" mobile/lib/features/scan/scan_repository.dart`). If absent, add it (small, additive — this file is also touched in C1 so it stays single-owner within Lane C):
> ```dart
> Future<Map<String, dynamic>> getDraft(int id) async {
>   try {
>     final res = await _dio.get<Map<String, dynamic>>('/scans/$id');
>     return res.data ?? <String, dynamic>{};
>   } on DioException catch (e) {
>     throw ApiException.fromDioException(e);
>   }
> }
> ```
> If a typed `ScanDraft` model + getter already exists, prefer returning its `.fields`/`.status` instead and adapt `_poll`/`_seed` accordingly.

**Verify:** `cd mobile && flutter analyze` → 0 errors.
**Commit:** `feat(scan-mobile): guided stepped New-rental-from-lease flow (mirror of web)`

### Step C3 — Extend `_CreatePropertyFields` + route legacy lease drafts into the guided flow

The existing `scan_review_screen.dart` `_CreatePropertyFields` (`:1539-1583`) has only 3 fields and no Places. Two changes:

1. **Extend `_CreatePropertyFields`** so the legacy review screen's create-property section also has state/zip and a Places-backed address (parity with the new flow), in case a Lease draft still lands there (e.g. deep links, older entry points). Add `property_state` and `property_postal_code` `_PlainFieldInput`s after `property_city`, and swap the `property_address` `_PlainFieldInput` for `AddressAutocompleteField` writing into `editedFields['property_address']` and, on resolve, filling `property_city`/`property_state`/`property_postal_code`. Import the widget: `import '../places/address_autocomplete_field.dart';`. (Bind the widget to a `TextEditingController` seeded from `editedFields['property_address']`; on change call `onFieldChanged('property_address', value)`.)

2. **Route Lease drafts to the guided flow.** Where the capture/scan entry currently opens `scan_review_screen` for a Lease draft, open `GuidedRentalFlow.open(context, draftId)` instead. `grep -n "ScanReviewScreen\|scan_review_screen\|targetEntityType == 'Lease'\|'Lease'" mobile/lib/features` to find the dispatch site(s). Keep `scan_review_screen` as the path for Expense/Payment/WorkOrder drafts.

**Verify:** `cd mobile && flutter analyze` → 0 errors.
**Commit:** `feat(scan-mobile): richer create-property fields + route lease drafts to the guided flow`

### Step C4 — Swap the Places widget into the 4 add-sheets' address fields (gap #4) + accept `LeasePrefill?`

For parity and to fulfill "swap it into the 4 add-sheets' address fields" — only the Property sheet has an address; Unit/Tenant/Lease sheets have none, so the Places swap applies to the **Property add-sheet** (and any other sheet with a street address). The `LeasePrefill?` ctor is the prefill hook for completeness, but the guided flow (C2) is the primary pre-fill surface; wiring `existing`-style prefill into the add-sheets is the secondary path.

1. **Property `_AddPropertySheet`** (`properties/properties_list_screen.dart:339-547`): replace the plain address `TextField`/`TextFormField` (the `_addressCtrl` field) with `AddressAutocompleteField(controller: _addressCtrl, onResolved: (a) { _cityCtrl.text = a.city; _stateCtrl.text = a.state; _zipCtrl.text = a.zip; })`. Import the widget. Leave `createProperty` (`:389-396`) untouched.
2. **Optional prefill ctors** (only if low-risk; else skip and rely on C2): give `_AddPropertySheet` and `_AddUnitSheet` an optional `LeasePrefill? prefill` ctor param that seeds the controllers in a guarded `initState`, matching how `_EditUnitSheet`/`TenantFormSheet`/`LeaseFormSheet` already seed from `existing`. Tenant/Lease sheets already accept `existing:` so no new ctor needed there.

> Keep this step SMALL and additive — the address-widget swap on the Property sheet is the must-do; the prefill ctors are nice-to-have and must not regress the existing manual create flow. If a sheet's address field is a `TextFormField` inside a `Form` with validators, wrap/adapt so validation still fires.

**Verify:** `cd mobile && flutter analyze` → 0 errors. Smoke (Lane C reviewer): open the Property add-sheet, confirm the address field still creates a property and the dropdown appears when a key is configured.
**Commit:** `feat(mobile): Google Places address field in the property add-sheet (existing proxy)`

---

## 4. Config preflight (operator note — include in the PR description, not a code step)

The flow degrades gracefully but is only fully functional when these are set per environment:
- `GooglePlaces:ApiKey` — `dotnet user-secrets set "GooglePlaces:ApiKey" "AIza…" --project RentalCommand.Api` (dev) or container env (prod). Without it, address autocomplete silently falls back to plain manual entry on BOTH clients (verified self-gating: `PlacesController` returns `enabled:false`; web `AddressAutocomplete` and the new mobile widget both handle it).
- `Assistant:Provider` / `Assistant:ModelId` / `Assistant:ApiKey` (section "Assistant", `AssistantConfig`) — required for the LLM extraction that pre-fills the forms. Without it, the upload still creates a draft but extraction yields no fields, so the steps render blank (still usable — the user types it in, which is the manual fallback).

---

## 5. Risks / devil's-advocate

- **Create-vs-link mislink / duplicate.** The Property step defaults to the proposal's link when the backend matched an existing property, else "create new," and shows a duplicate-guard hint when the user is on "create new" while properties exist (AC-4). Residual risk: address-normalization false-negatives (e.g. "Ave" vs "Avenue" already folded by `NormalizeAddress:1020-1059`, but "N Main" vs "North Main" edge cases) → a near-duplicate property. Mitigation in-scope: the explicit existing-property dropdown at Step 1 + the review screen. Out of scope: fuzzy-match scoring UI (file as a follow-up if QA finds dupes).
- **Multi-page stitch fidelity (orientation, order, size).** jspdf/pdf both choose page orientation per image and fit-to-page; order follows the picker's returned order (user controls capture order). Risk: a very large burst of high-res photos → a multi-MB PDF that bumps the upload size limit (`FileUploadValidator:9-113`) or the LLM's input budget. Mitigation: mobile re-encodes to JPEG q85 before embedding; web embeds as-is (jspdf compresses JPEG). If size becomes an issue, downscale before stitch (follow-up). Rotation metadata (EXIF) on web: browsers normalize via the `<img>` decode, so the rendered orientation is honored; on mobile, `image.decodeImage` bakes orientation. Acceptable for v1.
- **Places quota / abuse.** The proxy is rate-limited per IP+session (`PlacesRateLimiter`, 30/10s). The new mobile widget reuses one `session` token per typing burst and starts a fresh one after a details lookup (mirrors web), keeping Google's session-billing correct and the limiter happy. Risk: a chatty `onChanged` without debounce → 429s; mitigated by the 250ms debounce + 3-char minimum in the widget.
- **Factoring risk (shared field components without breaking the manual modals).** Each `*Fields` component owns ONLY field rows (no Dialog/submit), and each manual modal keeps its own `$state` form + `submit` + mutation. The refactor is mechanical replacement of inlined markup with the component bound to the SAME form object whose keys are unchanged. The svelte-check gate after EACH of B2–B5 catches a prop/键 mismatch immediately. Highest-risk modal is Lease (mixed pickers + terms): the plan keeps the pickers in the page and only lifts the term rows, and adds the missing `notes` key — verify the lease still creates after B5.
- **Two FileDrop instances on the capture screen (web).** Photos vs single-PDF are two separate `FileDrop`s; selecting in one doesn't clear the other's internal `selectedFile`, but only one triggers an upload and the page transitions to `processing` immediately, so there's no double-upload. Acceptable.
- **Mobile guided flow v1 has no existing-property LINK picker** (it always creates from the document), unlike web. This is a deliberate v1 scope cut noted in `_overridesJson`. The duplicate-guard parity is a fast follow-up (add a property dropdown to Step 1 fed by `propertiesProvider`, mirroring web). Flagged so QA doesn't file it as a bug — it's a known gap, and the server still dedupes by address match-or-create (`ResolveOrCreatePropertyAsync`), so it links rather than duplicates when the address matches.

---

## 6. "Big scan-in" end-to-end test (after the lanes land)

Exercise the whole front door against the local corpus `~/rental-sample-scans/` (32 docs; `leases/` has 7 lease PDFs + `strung-photos-delgado/` = phone photos of ONE lease). This is exploratory UI testing via playwright-cli + a human/agent walkthrough — do NOT author Playwright test files (per project testing discipline).

**Preflight:**
1. Ensure `GooglePlaces:ApiKey` + `Assistant:*` are set in the dev API (`dotnet user-secrets list --project RentalCommand.Api`).
2. `./scripts/start-dev.sh` (or the repo's dev launcher) so API + Engine + web are up; sign in as a test user with an EMPTY-ish portfolio (so the create path is exercised).

**Web walkthrough:**
1. Go to `/scan` → "New rental from your lease".
2. **Single PDF path:** drop `~/rental-sample-scans/leases/lease_fletcher_sunsetridge.pdf` into the PDF FileDrop. Expect: "Reading your lease…" → Step 1 of 4 · Property, pre-filled (Sunset Ridge / 482 Sunset Ridge Dr) with "from your lease" badges (AC-3). Verify the stepper header + progress bar (AC-1).
3. Step through: Property (try the existing-property dropdown shows the duplicate-guard if you pre-created Sunset Ridge), Unit (2 bed / 1.5 bath pre-filled), Tenant (Daniel Fletcher split into first/last), Lease ($1,575/mo, dates, due day). Edit a field at each step; confirm Back preserves edits (AC-1).
4. Review screen: confirm the summary line reads "Property … · Unit … · Tenant … · Lease $1,575/mo, 2026-07-01 – 2027-06-30" (AC-4). Tap Confirm & create → lands on `/leases/{id}`; verify in `/properties`, `/units`, `/tenants`, `/leases` that exactly ONE of each was created (no duplicates).
5. **Multi-photo path:** repeat using the Photos FileDrop, selecting ALL images in `~/rental-sample-scans/leases/strung-photos-delgado/` at once. Expect them stitched into one PDF → one draft → the same stepped flow.
6. **Dedup path:** run the SAME lease again; at Step 1 pick the now-existing property from the dropdown → confirm it LINKS (no second property) and the review shows "(existing)".
7. **Address autocomplete:** on the Property step, clear the street line and type a partial address → suggestions appear; pick one → city/state/zip auto-fill.

**Mobile walkthrough (emulator or device against the dev API):**
1. Capture FAB → "Scan a lease" → pick multiple photos from the `strung-photos-delgado` set (or the device camera) → expect the guided stepped screen (AppBar "Step 1 of 4 · Property"), pre-filled with "from your lease" badges (AC-3/AC-5).
2. Walk the 4 steps + review; on the Property step verify the Places dropdown works (with a key configured) and Back/Next preserve edits.
3. Confirm & create → snackbar "Your rental is set up." → verify the property/unit/tenant/lease exist and are not duplicated.

**Pass criteria:** every lease in `~/rental-sample-scans/leases/` can be driven through to a created Property+Unit+Tenant+Lease via the stepped flow on web; the multi-photo Delgado set produces one draft; re-running a lease links instead of duplicating; all five AC visibly hold on both clients; `svelte-check` and `flutter analyze` are clean.

---

## 7. Step count & ordering summary

- **Lane A (contracts/utils/docs):** A0, A1, A2, A3 = **4 steps** (do A1+A2 before B/C start coding consumers).
- **Lane B (web):** B1, B2, B3, B4, B5, B6, B7, B8 = **8 steps**.
- **Lane C (mobile):** C1, C2, C3, C4 = **4 steps**.
- **Total: 16 steps**, 16 commits. Lanes B and C run concurrently after A2; serialize only the actual `svelte-check`/`flutter analyze`/`dotnet` invocations (never two heavy builds at once, per project memory).

**Surfaces touched — WEB (each a distinct file):**
1. `web/src/lib/scan/lease-prefill.ts` (new, Lane A)
2. `web/src/lib/scan/stitch-pdf.ts` (new, Lane A)
3. `web/src/lib/components/forms/AutoFilledBadge.svelte` (new, B1)
4. `web/src/lib/components/forms/PropertyFields.svelte` (new, B2)
5. `web/src/lib/components/forms/UnitFields.svelte` (new, B3)
6. `web/src/lib/components/forms/TenantFields.svelte` (new, B4)
7. `web/src/lib/components/forms/LeaseTermFields.svelte` (new, B5)
8. `web/src/routes/(protected)/properties/+page.svelte` (refactor → PropertyFields, B2)
9. `web/src/routes/(protected)/properties/[id]/+page.svelte` (refactor → UnitFields, B3)
10. `web/src/routes/(protected)/tenants/+page.svelte` (refactor → TenantFields, B4)
11. `web/src/routes/(protected)/leases/+page.svelte` (refactor → LeaseTermFields + `notes` key, B5)
12. `web/src/lib/components/FileDrop.svelte` (multiple-file mode, B6)
13. `web/src/routes/(protected)/scan/new-rental/+page.svelte` (new guided flow, B7)
14. `web/src/routes/(protected)/scan/+page.svelte` (entry CTA, B8)
15. `web/src/lib/components/onboarding/LeasePhotoPrefill.svelte` (link to full flow, B8)

**Surfaces touched — MOBILE (each a distinct file):**
1. `mobile/lib/features/scan/lease_prefill.dart` (new, Lane A)
2. `mobile/lib/features/scan/pdf_stitch.dart` (new, Lane A)
3. `mobile/lib/features/places/places_repository.dart` (new, Lane A)
4. `mobile/lib/features/places/address_autocomplete_field.dart` (new, Lane A)
5. `mobile/pubspec.yaml` (add `pdf` + `image`, Lane A)
6. `mobile/lib/features/capture/capture_fab_sheet.dart` (multi-image + stitch, C1)
7. `mobile/lib/features/scan/guided_rental_flow.dart` (new stepped flow, C2)
8. `mobile/lib/features/scan/scan_repository.dart` (add `getDraft` if absent, C2)
9. `mobile/lib/features/scan/scan_review_screen.dart` (richer create fields + route lease → guided, C3)
10. `mobile/lib/features/properties/properties_list_screen.dart` (Places address in property add-sheet, C4)
11. `mobile/lib/features/properties/property_detail_screen.dart` (optional `LeasePrefill?` ctor, C4)

**Docs touched (Lane A):** `Docs/Reviews/2026-06-15-ease-of-use-restructure-audit.md` (B7 supersede note), `Docs/sample-scans/13-lease-sunset-ridge.txt` (new), `Docs/sample-scans/README.md` (row).
