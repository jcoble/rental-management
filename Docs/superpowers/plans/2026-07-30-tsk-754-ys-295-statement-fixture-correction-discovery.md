# TSK-754 YS-295 Statement Fixture Correction — Discovery Brief

## Control

- Discovery verdict: `ROADMAP DISCOVERY READY`
- Evidence reference: `YS295-FIXTURE-DISCOVERY-20260730-A`
- Planning attempt: `1`
- Configured numeric planning retry limit: not recorded. Stop only for a material decision, unexplained variance, unsafe mutation, or reviewer-authored failure routing.
- Active task: TSK-754 remains `Doing` / `High`.
- Active goal: resolve YS-295 append-only before `RUN-20270220-01` while preserving Payments 67/68. This brief covers only the prerequisite statement-fixture correction.
- Protected worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution`
- Starting HEAD: `8f0b3c5e1d0c0893161fccd85526133ec00f45a1`
- Protected state: 711-entry dirty shared WIP, live database, Payments 67/68, and all accepted past steps.
- Prior reviewed authority: `Docs/superpowers/plans/2026-07-30-tsk-754-ys-295-loan-payment-reconciliation-execution.md`, SHA-256 `7ff6bdb3a62e3628b8b2dc956fe924002915c4c11e5e2b0c41c7a01e1c557404`.

## Active numbered step

1. Correct only the authoritative fixture inputs and rendered statements for `SCN-0478` / `FIN-01121` and `SCN-0479` / `FIN-01122`. Add one optional opening-unpaid-principal-in-cents input to the existing `Asset` and scan-manifest schema, populate it only with `12539400` for `SCN-0478` and `13045800` for `SCN-0479`, and make the existing renderer use it as opening unpaid principal, derive ending unpaid principal as opening minus event principal, and render distinct opening and ending labels.

Every later YS-295 database correction, match, duplicate-prevention proof, and scheduled simulation boundary remains deferred.

## Existing ownership

- `scripts/qa/generate-year-simulation-plan.py`
  - `Asset`, lines 198–216, owns the fixture-input schema.
  - `add_asset`, lines 424–464, owns manifest serialization inputs.
  - `build_recurring_and_owner_finance`, lines 1225–1269, owns mortgage event and statement creation.
- `Docs/Testing/YearSimulation2027/scan-assets.csv`, rows 479–480, owns the two fixture inputs. Both descriptions currently identify the balance as `ending principal balance`.
- `scripts/qa/render-year-simulation-scan-corpus.py`
  - `data_context`, lines 184–296, owns statement values.
  - Lines 247–257 already source principal, interest, escrow, and total from the FIN events.
  - Line 284 incorrectly reuses the immutable opening loan balance from `portfolio.csv`.
  - `field_rows`, lines 485–533, owns visible labels and currently renders that wrong source as `Unpaid principal balance`.
  - `render_all`, lines 1438–1501, and `main`, lines 1614–1631, already support selected `--asset-id` rendering without rebuilding corpus indexes.
- `scripts/qa/test_render_year_simulation_scan_corpus.py`, `BankStatementCorpusTests`, especially lines 96–125, is the focused mortgage-context test owner.
- `Docs/Testing/YearSimulation2027/financial-oracle.csv`, rows 1122–1123, already contains the correct FIN splits and must remain unchanged.
- `Docs/Testing/YearSimulation2027/journal.csv`, rows 2287–2294, already contains the correct four-line journals and must remain unchanged.
- `RentalCommand.Data/Scanning/ProductionScanConfirmationTargetWriter.cs`, lines 1137–1143, already matches opening balance as `BalanceAfter + PrincipalAmount` and must remain unchanged.
- History `6116379e` introduced the renderer and portfolio-opening selection; `85236df9` retained that selection while refining other rendering.
- No fixture-override file or service exists. The existing manifest and renderer are the source-owned correction points.

## Exact statement truth

| Asset / event | Opening unpaid principal | Principal | Interest | Escrow | Total | Derived ending unpaid principal |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `SCN-0478` / `FIN-01121` | 125,394 | 431 | 615 | 318 | 1,364 | 124,963 |
| `SCN-0479` / `FIN-01122` | 130,458 | 442 | 628 | 318 | 1,388 | 130,016 |

The opening value must be labeled `Opening unpaid principal`, not as an ending balance. The derived result must be separately labeled `Ending unpaid principal`.

## Supported flow

A fixture operator changes only the existing schema, the two authoritative manifest rows, the existing renderer, and its focused test. The operator then renders only `SCN-0478` and `SCN-0479`, extracts both PDFs' text, renders page 1 of each, and checks exact values, labels, three-page letter layout, and extractable `SCN` / `SYNTHETIC` text. The acceptance verifier visually inspects both page-1 PNGs with the local image viewer. This boundary ends before any web, mobile, device, live-database, statement-match, or duplicate-prevention flow.

## Exact planning and execution surfaces

Modify only:

- `scripts/qa/generate-year-simulation-plan.py`
- `Docs/Testing/YearSimulation2027/scan-assets.csv`
- `scripts/qa/render-year-simulation-scan-corpus.py`
- `scripts/qa/test_render_year_simulation_scan_corpus.py`

Regenerate only:

- `output/pdf/tsk-749-year-simulation-scan-corpus/documents/2027-02-20/scn-0478-loan-payment-ln001-202702.pdf`
- `output/pdf/tsk-749-year-simulation-scan-corpus/documents/2027-02-20/scn-0479-loan-payment-ln002-202702.pdf`

Acceptance proof may create only:

- `output/qa/tsk754-evidence/YS-295-SCN-0478-text.txt`
- `output/qa/tsk754-evidence/YS-295-SCN-0479-text.txt`
- `output/qa/tsk754-evidence/YS-295-SCN-0478-page-1.png`
- `output/qa/tsk754-evidence/YS-295-SCN-0479-page-1.png`

Planning paths:

- This brief: `Docs/superpowers/plans/2026-07-30-tsk-754-ys-295-statement-fixture-correction-discovery.md`
- Execution contract: `Docs/superpowers/plans/2026-07-30-tsk-754-ys-295-statement-fixture-correction-execution.md`

## Preserved baselines

- `Docs/Testing/YearSimulation2027/financial-oracle.csv`: `473e672caa86d40cee5ac7b811cfe06b80483b4cd2b6febfeeee4bd38eae80ec`
- `Docs/Testing/YearSimulation2027/journal.csv`: `033fe84423fe789aa25c95ddc7452e8ff64e4b9cdfb3a294d653ca8e23964133`
- `RentalCommand.Data/Scanning/ProductionScanConfirmationTargetWriter.cs`: `a4175cdbdf5d56562aa69270d7e061a9ed1f75b45a0ae041d60eb1c851df2a9b`

## Non-goals and deferred boundaries

- No `SCN-0480` or later asset and no other statement or loan.
- No broad plan or corpus regeneration and no corpus index or report modification.
- No change to `FIN-01121`, `FIN-01122`, `financial-oracle.csv`, `journal.csv`, or matcher behavior.
- No new override file, service, abstraction, scaffold, fallback, compatibility lane, migration, broad test, hardening, or hypothetical edge-case requirement.
- No hand-edited PDF.
- No browser, mobile, or device proof.
- No Payments 67/68 change and no live-database mutation.
- No change to accepted past steps or later schedule execution.
- No unrelated cleanup, git or worktree operation, or alteration of unrelated dirty WIP.

## Decisions and future impact

No material decision remains. Assets without the optional input retain their existing rendering behavior, so this boundary creates no future-step planning impact.
