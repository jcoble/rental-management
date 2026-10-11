# TSK-754 YS-295 Statement Fixture Correction — Execution Contract

## Contract control

- Contract type: fresh executable next-boundary contract.
- Discovery authority: `YS295-FIXTURE-DISCOVERY-20260730-A`.
- Planning attempt: `1`.
- Configured numeric planning retry limit: not recorded. Stop only for a material decision, unexplained variance, unsafe mutation, or reviewer-authored failure routing.
- Active task: TSK-754 remains `Doing` / `High`.
- Protected worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution`.
- Starting HEAD: `8f0b3c5e1d0c0893161fccd85526133ec00f45a1`.
- Protected state: preserve the 711-entry dirty shared WIP, live database, Payments 67/68, accepted past steps, and all unrelated files.
- Execution begins only after a fresh `roadmap-contract-reviewer` returns `PLAN CONTRACT PASS`.
- After approval, route through the existing inner loop: `plan-recovery-custodian` → `scope-implementer` → fresh `relevance-reviewer` → fresh `acceptance-verifier` → `contract-fixer` only on a role-authored failure → fresh review and verification → terminal `plan-recovery-custodian`.
- Run no heavy build or test concurrently.

## Active numbered step

1. Correct only the `SCN-0478` / `FIN-01121` and `SCN-0479` / `FIN-01122` statement fixtures by adding one optional opening-unpaid-principal-in-cents input to the existing `Asset` and scan-manifest schema, setting it only to `12539400` for `SCN-0478` and `13045800` for `SCN-0479`, and updating the existing renderer to consume that input, derive ending unpaid principal as opening unpaid principal minus event principal, and render distinct `Opening unpaid principal` and `Ending unpaid principal` rows.

All later YS-295 append-only database corrections, matches, duplicate-prevention checks, and schedule boundaries remain deferred.

## Allowed implementation surface

Modify only:

| File | Exact permitted change | Acceptance mapping |
| --- | --- | --- |
| `scripts/qa/generate-year-simulation-plan.py` | Add exactly one optional opening-unpaid-principal-in-cents input to the existing `Asset` / `add_asset` manifest schema and populate it only for `SCN-0478=12539400` and `SCN-0479=13045800`. | A1, A2, A3 |
| `Docs/Testing/YearSimulation2027/scan-assets.csv` | Add the matching optional input column; set only the two named values; change only the two named expected-field descriptions to `opening unpaid principal; ending unpaid principal`. | A1, A2, A3 |
| `scripts/qa/render-year-simulation-scan-corpus.py` | Read the optional input. When present, expose it as opening unpaid principal, derive ending unpaid principal by subtracting FIN event principal, and render distinct opening and ending rows. Preserve current behavior when it is absent. | A1–A7, A10 |
| `scripts/qa/test_render_year_simulation_scan_corpus.py` | Add focused `selected_february_mortgage` coverage for the two manifest inputs, contexts, labels, exact amounts, derived endings, and unchanged behavior for assets without the optional input. | A1–A5, A10 |

Do not create any implementation file.

## Allowed generated and proof surface

Regenerate only:

| Generated output | Acceptance mapping |
| --- | --- |
| `output/pdf/tsk-749-year-simulation-scan-corpus/documents/2027-02-20/scn-0478-loan-payment-ln001-202702.pdf` | A3, A5–A7, A10 |
| `output/pdf/tsk-749-year-simulation-scan-corpus/documents/2027-02-20/scn-0479-loan-payment-ln002-202702.pdf` | A4–A7, A10 |

Acceptance proof may create only:

| Proof output | Acceptance mapping |
| --- | --- |
| `output/qa/tsk754-evidence/YS-295-SCN-0478-text.txt` | A3, A5, A7 |
| `output/qa/tsk754-evidence/YS-295-SCN-0479-text.txt` | A4, A5, A7 |
| `output/qa/tsk754-evidence/YS-295-SCN-0478-page-1.png` | A6 |
| `output/qa/tsk754-evidence/YS-295-SCN-0479-page-1.png` | A6 |

No corpus index, corpus report, `SCN-0480` or later PDF, or other generated/proof output may be created or modified.

## Supported execution flow

The scope implementer first checks the focused acceptance case, changes only the four allowed implementation files if the numbered acceptance items are not already satisfied, and runs only the selected renderer for the two named assets. The fresh acceptance verifier independently reruns the focused test and selected render, extracts both PDFs, renders page 1 of both, checks the exact text and PDF properties, checks all three preserved hashes, and visually inspects both PNGs using the local image viewer. No browser, mobile, device, or live-database flow is part of this contract.

## Exact commands

### C1 — focused fixture and renderer test

Maps to A1–A5 and A10.

```bash
python3 -m unittest scripts/qa/test_render_year_simulation_scan_corpus.py -k selected_february_mortgage
```

### C2 — render only the two selected statements

Maps to A2–A7 and A10.

```bash
python3 scripts/qa/render-year-simulation-scan-corpus.py \
  --asset-id SCN-0478 \
  --asset-id SCN-0479
```

The observed renderer result must report `asset_count: 2`, both `SCN-0478` and `SCN-0479`, and `SELECTED_FILES_VALIDATED`.

### C3 — extract both complete PDFs

Maps to A3–A5 and A7.

```bash
pdftotext -layout \
  output/pdf/tsk-749-year-simulation-scan-corpus/documents/2027-02-20/scn-0478-loan-payment-ln001-202702.pdf \
  output/qa/tsk754-evidence/YS-295-SCN-0478-text.txt

pdftotext -layout \
  output/pdf/tsk-749-year-simulation-scan-corpus/documents/2027-02-20/scn-0479-loan-payment-ln002-202702.pdf \
  output/qa/tsk754-evidence/YS-295-SCN-0479-text.txt
```

### C4 — render page 1 of both PDFs

Maps to A6 and A7.

```bash
pdftoppm -f 1 -l 1 -singlefile -png -r 150 \
  output/pdf/tsk-749-year-simulation-scan-corpus/documents/2027-02-20/scn-0478-loan-payment-ln001-202702.pdf \
  output/qa/tsk754-evidence/YS-295-SCN-0478-page-1

pdftoppm -f 1 -l 1 -singlefile -png -r 150 \
  output/pdf/tsk-749-year-simulation-scan-corpus/documents/2027-02-20/scn-0479-loan-payment-ln002-202702.pdf \
  output/qa/tsk754-evidence/YS-295-SCN-0479-page-1
```

### C5 — inspect both PNGs with the local image viewer

Maps to A6.

The fresh acceptance verifier opens and visually inspects:

- `output/qa/tsk754-evidence/YS-295-SCN-0478-page-1.png`
- `output/qa/tsk754-evidence/YS-295-SCN-0479-page-1.png`

This is local image proof, not browser, mobile, or device proof.

## Numbered observable acceptance

### A1 — Optional input is narrow and source-owned

The existing `Asset` and scan-manifest schema has exactly one optional opening-unpaid-principal-in-cents input. Only `SCN-0478` carries `12539400`, only `SCN-0479` carries `13045800`, and every other asset retains existing behavior.

### A2 — Selected rendering is exact

The selected renderer reports `asset_count: 2`, identifies both `SCN-0478` and `SCN-0479`, and reports `SELECTED_FILES_VALIDATED`. It regenerates only the two allowed PDFs.

### A3 — SCN-0478 statement truth

The `SCN-0478` extracted text contains:

- opening unpaid principal `125,394`;
- principal `431`;
- interest `615`;
- escrow `318`;
- total or amount due `1,364`;
- derived ending unpaid principal `124,963`.

### A4 — SCN-0479 statement truth

The `SCN-0479` extracted text contains:

- opening unpaid principal `130,458`;
- principal `442`;
- interest `628`;
- escrow `318`;
- total or amount due `1,388`;
- derived ending unpaid principal `130,016`.

### A5 — Labels are explicit and separate

Both extracted text files contain `OPENING UNPAID PRINCIPAL` and a separate `ENDING UNPAID PRINCIPAL`. Neither text file contains `ending principal balance`, case-insensitively.

### A6 — Page-1 visual proof

The fresh acceptance verifier visually inspects both page-1 PNGs with the local image viewer and observes that the correct statement contains its exact opening value, payment split, total, derived ending value, and distinct readable opening/ending labels.

### A7 — PDF contract remains intact

Each selected PDF remains a three-page US-letter PDF. Complete text extraction succeeds and contains extractable `SCN` and `SYNTHETIC` text associated with the correct statement.

### A8 — Financial source files are byte-identical

- `Docs/Testing/YearSimulation2027/financial-oracle.csv` remains SHA-256 `473e672caa86d40cee5ac7b811cfe06b80483b4cd2b6febfeeee4bd38eae80ec`.
- `Docs/Testing/YearSimulation2027/journal.csv` remains SHA-256 `033fe84423fe789aa25c95ddc7452e8ff64e4b9cdfb3a294d653ca8e23964133`.

### A9 — Product matcher is byte-identical

`RentalCommand.Data/Scanning/ProductionScanConfirmationTargetWriter.cs` remains SHA-256 `a4175cdbdf5d56562aa69270d7e061a9ed1f75b45a0ae041d60eb1c851df2a9b`.

### A10 — Nothing outside the boundary moves

No PDF from `SCN-0480` onward, no corpus index or report, no other statement/loan fixture, and no unrelated dirty WIP is created, regenerated, or modified.

## Non-goals

- Other statements or loans, including `SCN-0480` through `SCN-0497`.
- Broad plan regeneration or broad scan-corpus regeneration.
- Any change to `FIN-01121`, `FIN-01122`, `financial-oracle.csv`, `journal.csv`, FIN splits, or journal splits.
- Any change to matcher logic or `ProductionScanConfirmationTargetWriter.cs`.
- A new override file or service, manual PDF editing, scaffolding, abstraction, fallback, compatibility lane, migration, broad test, hardening, or hypothetical edge-case work.
- Browser, mobile, or device proof.
- Payments 67/68 or any live-database mutation.
- Accepted past steps, later schedule execution, or the remaining YS-295 database/matching/duplicate-prevention work.
- Unrelated cleanup or any git/worktree operation.
- Resetting, stashing, cleaning, switching branches, committing, pushing, removing the worktree, or altering unrelated dirty WIP.

## Completion and routing

This contract is complete only after a fresh `relevance-reviewer` returns in-contract approval, a fresh `acceptance-verifier` independently proves A1–A10 including both PNG inspections, and the terminal `plan-recovery-custodian` records the accepted checkpoint. Until then, later YS-295 and schedule boundaries remain deferred.

The writer's next route is a fresh `roadmap-contract-reviewer`.
