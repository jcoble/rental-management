# 2027 Scan-First Rental Company Simulation

Open `index.html` first. It is the day-by-day execution contract for TSK-749.

The CSVs and `rental-command-2027-simulation-oracle.xlsx` are the independent
controls. Do not change amounts or dates during execution without issuing a
versioned planner correction and regenerating every dependent control.

`scan-assets.csv` is the source manifest for the rendered synthetic fixture corpus.
Generate it with `scripts/qa/render-year-simulation-scan-corpus.py`; the default
destination is `output/pdf/tsk-749-year-simulation-scan-corpus/`. Use the dated
folders under `documents/` with the matching simulated clock day. The generated
`corpus-index.csv` records the checksum and validation result for every upload file.
No real documents, identities, signatures, accounts, or production data are permitted.

`screen-inventory.csv`, `field-inventory.csv`, `role-journeys.csv`,
`crud-lifecycles.csv`, and `notification-scenarios.csv` are regenerated from the
current product source and the exhaustive operational contract. Run the generator
again immediately before executing the year so newly added screens or fields do
not escape coverage.
