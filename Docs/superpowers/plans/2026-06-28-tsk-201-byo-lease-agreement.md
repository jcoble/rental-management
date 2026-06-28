# TSK-201 - BYO Lease Agreement Templates and Field-Aware E-Sign

Date: 2026-06-28
Branch: cdx/tsk-201-lease-template-system
Task: TSK-201

## Decision

Build the landlord-owned lease template system in this order:

1. Option B first: landlord uploads their existing lease PDF, the app preserves the exact PDF, and we overlay dynamic values plus signer fields at page coordinates.
2. Option A second: landlord drafts or edits lease content in an in-app editor, then places the same dynamic fields onto the generated agreement.
3. E-sign is field-aware: signing requests must know signer roles, tabs/anchors, required signature/initial/date fields, where the executed PDF is stored, and how lease status changes.

This matches the owner decision in TSK-201: established landlords will not want to throw away a legal lease they already paid to refine. The first version must respect their PDF.

## Current State

- `RentalCommand.Api/Services/Domain/LeaseAgreementPdfGenerator.cs` creates a fixed QuestPDF residential lease from `LeaseAgreementData`.
- `RentalCommand.Api/Services/Domain/LeaseEsignService.cs` sends that generated PDF to the configured provider and stores the final signed document on completion.
- `RentalCommand.Core/Interfaces/IEsignProvider.cs` only accepts `DocumentBytes` and signer name/email. It has no field/tabs/role model.
- `RentalCommand.Api/Services/Esign/NativeSigningService.cs` and `ExecutedLeasePdfGenerator.cs` append typed/drawn signatures and a certificate. They do not stamp signatures into PDF coordinates.
- `RentalCommand.Api/Scanning/PdfTextExtractor.cs` can extract born-digital text via PdfPig. The current `FieldExtraction.SourceBox` type exists, but active provider parsers do not populate it.
- `web/src/lib/components/records/LeaseDetail.svelte` has only generate, download, send, and download signed copy controls. There is no template library or placement editor.

## Vendor Scan

The third-party tools can help, but they should not own our template model:

- Dropbox Sign is already partly integrated. Official docs support placing fields on documents with `form_fields_per_document`, and embedded templates allow users to create/edit templates in an iframe. This is the lowest switching cost option if pricing/account flow is acceptable.
  - https://help.dropbox.com/integrations/how-to-use-the-form-fields-per-document-parameter
  - https://developers.hellosign.com/docs/walkthroughs/embedded-templates
- DocuSign has mature tabs/templates, embedded sending, embedded signing, and auto-place anchor tabs. It is the strongest enterprise e-sign provider, but switching from the existing Dropbox Sign seam is more work.
  - https://developers.docusign.com/docs/esign-rest-api/esign101/concepts/tabs/
  - https://developers.docusign.com/docs/esign-rest-api/how-to/embedded-sending/
  - https://developers.docusign.com/docs/esign-rest-api/esign101/concepts/tabs/auto-place/
- PandaDoc can create documents from PDF/DOCX/RTF, pre-fill fields, and send for signature. It is broader document automation, likely more product than we need for the first Rental Command slice.
  - https://developers.pandadoc.com/docs/create-document-from-file
  - https://developers.pandadoc.com/docs/form-fields
- DocuSeal is attractive for open-source/self-hosted style templates and submissions, but adopting it would add another platform to operate.
  - https://www.docuseal.com/docs/api
  - https://www.docuseal.com/guides/pre-fill-pdf-document-form-fields-with-api

Recommendation: build Rental Command's own field anchor and template version model, then adapt providers. Use the native provider for an end-to-end v1. Extend Dropbox Sign next because code already exists. Keep DocuSign as a later provider option if production e-sign requirements outgrow Dropbox/native.

## Legal/Compliance Guardrails

This is not legal advice, but the implementation must preserve the evidence trail:

- The E-SIGN Act says electronic signatures/records cannot be denied effect solely because they are electronic, but it does not remove other legal requirements.
- Consumer consent needs clear disclosure, paper/right-to-withdraw language, contact update procedure, hardware/software requirements, and electronic consent that demonstrates access.
- The app already captures consent, IP, user agent, timestamps, signed document hash, and audit events. TSK-201 must keep that trail when signatures move from appended blocks to placed fields.

References:
- https://uscode.house.gov/view.xhtml?edition=prelim&path=%2Fprelim%40title15%2Fchapter96
- https://www.fdic.gov/consumer-compliance-examination-manual/x-3-electronic-signatures-global-and-national-commerce-act-e

## Domain Model

Add template entities that are portfolio-scoped and versioned.

`DocumentTemplate`
- `Id`
- `PortfolioId`
- `Kind`: `Lease`, later `RentalApplication`
- `Name`
- `Status`: `Draft`, `Active`, `Archived`
- `RenderMode`: `Overlay`, later `Restyle`
- `OriginalStoredFileId`: uploaded source PDF for overlay mode
- `DraftHtml`: in-app drafted content for restyle/editor mode
- `CompiledStoredFileId`: optional generated source PDF for drafted mode
- `DefaultForPortfolio`
- `PropertyId`: optional per-property default
- `Version`
- `CreatedAtUtc`, `UpdatedAtUtc`, `ArchivedAtUtc`

`DocumentTemplateField`
- `Id`
- `DocumentTemplateId`
- `FieldKey`: for example `tenant.fullName`, `lease.monthlyRent`, `property.address`
- `Label`
- `Kind`: `Text`, `Currency`, `Date`, `Checkbox`, `Signature`, `Initial`, `DateSigned`
- `SignerRole`: `None`, `Tenant`, `Landlord`, later `CoTenant`
- `PageNumber`
- `XPct`, `YPct`, `WidthPct`, `HeightPct`: normalized top-left origin coordinates
- `Required`
- `Locked`
- `SortOrder`
- `DefaultText`

`Lease`
- Add `DocumentTemplateId`
- Add `DocumentTemplateVersion`
- Keep `SignedDocumentStoredFileId` and `EsignEnvelopeId`

`SignatureRequest`
- Add `TemplateFieldSnapshotJson` or a normalized child table so each request freezes the exact fields used when sent.
- Add signer role metadata so one envelope can later distinguish landlord and tenant fields.

## Field Catalog

Lease v1 catalog:

- `landlord.name`
- `tenant.fullName`
- `tenant.email`
- `property.name`
- `property.address`
- `unit.number`
- `lease.startDate`
- `lease.endDate`
- `lease.monthlyRent`
- `lease.securityDeposit`
- `lease.lateFeeAmount`
- `lease.rentDueDay`
- `lease.signature.tenant`
- `lease.signature.landlord`
- `lease.initials.tenant`
- `lease.dateSigned.tenant`
- `lease.dateSigned.landlord`

The catalog is server-owned. The editor may show friendly labels, but render/sign must use stable keys.

## API Surface

Phase 0/1 endpoints:

- `GET /api/v1/document-templates?kind=Lease`
- `POST /api/v1/document-templates/upload`
- `GET /api/v1/document-templates/{id}`
- `PATCH /api/v1/document-templates/{id}`
- `POST /api/v1/document-templates/{id}/fields`
- `PUT /api/v1/document-templates/{id}/fields/{fieldId}`
- `DELETE /api/v1/document-templates/{id}/fields/{fieldId}`
- `POST /api/v1/document-templates/{id}/activate`
- `GET /api/v1/document-templates/field-catalog?kind=Lease`
- `GET /api/v1/document-templates/{id}/source`
- `POST /api/v1/leases/{id}/render-document`
- Existing `POST /api/v1/leases/{id}/send-for-signature` should accept an optional `documentTemplateId` once rendering is template-aware.

All list/filter/sort/paging must stay DB-side per project SQL rules.

## Rendering

Overlay mode:

1. Load the uploaded source PDF from `StoredFile`.
2. Resolve field values from the selected lease in one DB query that includes property, unit, tenant, and portfolio/owner context.
3. Stamp text/date/currency values onto the PDF at normalized coordinates.
4. Preserve the exact landlord PDF content.
5. Store the rendered unsigned agreement as `StoredFile` with `EntityType=LeaseAgreement`.

Implementation direction:

- Keep PdfPig for text extraction and page sizing.
- Add a permissive PDF stamping library, likely PDFsharp, after license verification.
- Use normalized top-left coordinates in the app. Convert to PDF points only at the renderer boundary.
- Keep QuestPDF as the existing fallback/default generated lease and later restyle renderer.

## Field Placement Editor

Web only for v1:

- Template library page under the protected app.
- Upload PDF flow.
- PDF viewer using `pdfjs-dist` or a comparable viewer package.
- Field palette on the side with mapped/unplaced fields.
- Click/drag/drop field placement with resizable field boxes.
- Required signature field validation before activation/send.
- Preview mode that fills fields from a sample lease.

Mobile can consume and preview generated PDFs, but not edit field placement in v1.

## E-Sign Plan

Provider request must become field-aware:

- Signers include name, email, and role.
- Request includes document fields, signer fields, and immutable template version.
- Native provider stores the original rendered PDF and exposes the public signing page.
- On native signing, the executed PDF stamps the captured signature/date onto the configured signature/date fields and appends the existing certificate page.
- Dropbox Sign provider maps our anchors to Dropbox `form_fields_per_document`.
- Future DocuSign provider maps anchors to DocuSign tabs.

Status flow remains:

1. Lease Draft -> Send for signature.
2. Create signature request/envelope.
3. Lease `EsignStatus=Sent`, `LeaseStatus=PendingSignature`.
4. Signer signs or declines.
5. On completed/signed webhook or native completion, store signed PDF, set `EsignStatus=Signed`, and activate lease if pending.
6. On decline, set `EsignStatus=Declined` and keep the lease signable again.

## Phases

### Phase 0 - Foundation

- Add enums/entities/DbSets/migration for document templates and fields.
- Add lease field catalog service.
- Add DTOs and controller skeleton for template list/detail/catalog.
- Add tests for catalog and coordinate validation.

### Phase 1 - Exact PDF Overlay for Leases

- Upload source PDF as a document template.
- Save/update field anchors.
- Render a lease agreement from selected template.
- Add server tests proving values land on original PDF coordinates.

### Phase 2 - Template Designer UI

- Add template library and placement editor.
- Render PDF pages in-browser.
- Add field palette, drag/drop, resize, save, activate, and preview.
- Run `pnpm check` and a focused browser smoke test.

### Phase 3 - Field-Aware Native Signing

- Snapshot template fields on send.
- Require tenant signature anchor before send.
- Stamp typed/drawn signatures into configured coordinates.
- Keep certificate page and audit trail.

### Phase 4 - Hosted Provider Upgrade

- Extend Dropbox Sign adapter to send field coordinates.
- Compare DocuSign if production needs are stricter than Dropbox/native.

### Phase 5 - Drafted Lease Editor

- Add in-app drafting/editor path.
- Generate a source PDF from drafted content.
- Reuse the same placement editor and signing pipeline.

## First Build Slice

Start with Phase 0:

- `DocumentTemplate`, `DocumentTemplateField`, supporting enums.
- DbContext mapping and migration.
- `LeaseDocumentFieldCatalog` service.
- Controller endpoints for list/detail/catalog.
- No PDF stamping yet; the goal is to land the durable model and API seam first.

