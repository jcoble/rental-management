# E2E Scenario Corpus

Fictional-but-reconcilable rental business for an end-to-end test of the Rental Command **web**
app. Design + rationale live in
[`Docs/superpowers/specs/2026-06-30-e2e-scenario-corpus-design.md`](../../Docs/superpowers/specs/2026-06-30-e2e-scenario-corpus-design.md).
This folder holds the **generators, templates, ground-truth ledger, and expected figures**.

Status: **Phase-2 steps 1–3 COMPLETE** — the full CY2023–25 ground-truth ledger, per-report
expected figures (all 13 properties reconcile to the cent), and 110 scan artifacts are generated.
Resume/handoff state: **`SA-STATE.md`**.

## Layout

```
scenario/scenario.json   FROZEN business: 13 properties / 21 units / 11 loans / 22 leases / 4 owners
                         + recurring templates + story beats. Single source of truth for the pipeline.
templates/     HTML templates with {{tokens}} (lease, lease-photo-page, receipt, rent-check,
               mortgage-statement, application, maintenance-photo)
tools/
  emit-finance/          C# dev tool (refs RentalCommand.Core; NOT in .sln) — runs the app's real
                         Amortization/Depreciation calculators → authoritative ledger/*.csv
  build-ledger.mjs       scenario + finance CSVs → ledger/events.csv (the event log)
  derive-expected.mjs    events.csv → expected/*.json + divergence self-checks (Phase-5 authority)
  build-manifest.mjs     scenario → tools/data.full.json (the 110-artifact manifest)
  generate.mjs           renders a manifest to PDFs/JPEGs via Chrome + poppler (zero npm deps)
ledger/        loans.csv, loan-payments.csv, depreciation.csv (authoritative) + events.csv (ground truth)
expected/      cy2025-by-property.json, cy2025-portfolio.json, spotcheck-2023-2024.json
generated/     rendered scan artifacts (git-ignored; regenerable)  <-- binaries live here
```

## Full pipeline (regenerate everything deterministically)

```bash
cd e2e/corpus
# 1. authoritative finance (ONE dotnet build — coordinate with the serialized build slot):
MSBUILDDISABLENODEREUSE=1 dotnet run --project tools/emit-finance -- scenario/scenario.json ledger
dotnet build-server shutdown
# 2. ground-truth event log:      node tools/build-ledger.mjs
# 3. expected figures + checks:   node tools/derive-expected.mjs   # prints "ALL PASS" when it ties out
# 4. artifacts:                   node tools/build-manifest.mjs && node tools/generate.mjs tools/data.full.json
```

Revising the scenario (e.g. after the 7 domain features land) = edit `scenario.json`, re-run 1–4.

## Generation pipeline (tooling detected on this Mac, zero heavyweight installs)

- **Google Chrome headless** — `--print-to-pdf` for born-digital PDFs (exercises the scanner's
  text path; `pdftotext`-recoverable), `--screenshot=out.jpg` for "phone photo" JPEGs (vision
  path). Chrome writes **real JPEG** when the path ends in `.jpg`, adding realistic
  phone-camera compression artifacts.
- **poppler** — `pdftoppm` (raster), `pdfunite` (merge), `pdftotext` (verify extraction).
- **`sips`** (macOS built-in) — optional resize/recompress to hit a footprint budget.

Phone-photo realism is pure CSS (paper card on a dark desk, rotation, drop shadow, vignette,
slight blur). Not installed / not needed: LibreOffice, wkhtmltopdf, ImageMagick, Pillow, qpdf.

`generate.mjs` manifest item: `{ template, out, mode: "pdf"|"photo", size?: [w,h], data: {token: value} }`
(paths relative to `e2e/corpus/`). `mode:"pdf"` → born-digital PDF (text/scanner path);
`mode:"photo"` → JPEG (name the `out` `*.jpg`; vision path). `tools/data.proof.json` is the small
Phase-1 proof manifest; `tools/data.full.json` is the full 110-artifact manifest.

## Ground truth = an event log, not pre-summed totals

The app defines "income"/"expense" **12+ different ways** across its reports (see design doc §8.3).
So ground truth records every money event **once** in `ledger/events.csv` with full attributes
(type, status, due/paid dates, category, escrow flag, amount, amount_paid); each report's expected
figure is a **derived view** matching that report's exact predicate, stored in `expected/*.json`.

`tools/derive-expected.mjs` computes each report's expected figure and **self-checks four
deliberate divergences per property** (asserted, not bug-flagged): P&L−ScheduleE by
interest+depreciation, plus any `Other`-type income (e.g. the $35 NSF fee) that P&L counts but
Schedule E doesn't (DIV1); escrowed taxes/insurance excluded from NOI opex (DIV2); cash-flow vs
taxable net (DIV3). It prints `ALL PASS (DIV1/DIV2/DIV3 tie to the cent)` across all 13 properties.

## Footprint

**110 artifacts, 9.5 MB**: 22 lease PDFs + 6 photo-stitch JPEGs, 11 mortgage JPEGs, 48 rent-check
JPEGs, 14 receipt JPEGs, 4 application PDFs, 5 maintenance JPEGs. Photos are JPEG (Chrome direct;
PNG would be ~8× larger). `generated/` is git-ignored; only text (`scenario/ templates/ tools/
ledger/ expected/`) is committed.
