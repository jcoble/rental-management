#!/usr/bin/env node

import fs from "node:fs/promises";
import path from "node:path";

const artifactToolModule = process.env.CODEX_ARTIFACT_TOOL_MODULE ?? "@oai/artifact-tool";
const { SpreadsheetFile, Workbook } = await import(artifactToolModule);

const [inputDir, primaryOutputDir, repoOutputDir] = process.argv.slice(2);
if (!inputDir || !primaryOutputDir || !repoOutputDir) {
  throw new Error("Usage: build-year-simulation-workbook.mjs <input-dir> <primary-output-dir> <repo-output-dir>");
}

const currencyFormat = '"$"#,##0;[Red]("$"#,##0);-';
const integerFormat = '#,##0;[Red](#,##0);-';
const dateFormat = "yyyy-mm-dd";
const dark = "#173F5F";
const teal = "#2A7F78";
const gold = "#D49B2A";
const pale = "#F5F1E8";
const green = "#008000";
const blue = "#0000FF";
const red = "#A84435";
const line = "#D9D1C4";

function parseCsv(text) {
  const rows = [];
  let row = [];
  let field = "";
  let quoted = false;
  for (let index = 0; index < text.length; index += 1) {
    const char = text[index];
    if (quoted) {
      if (char === '"' && text[index + 1] === '"') {
        field += '"';
        index += 1;
      } else if (char === '"') {
        quoted = false;
      } else {
        field += char;
      }
    } else if (char === '"') {
      quoted = true;
    } else if (char === ",") {
      row.push(field);
      field = "";
    } else if (char === "\n") {
      row.push(field.replace(/\r$/, ""));
      rows.push(row);
      row = [];
      field = "";
    } else {
      field += char;
    }
  }
  if (field.length || row.length) {
    row.push(field);
    rows.push(row);
  }
  const headers = rows.shift();
  return rows
    .filter((values) => values.some((value) => value !== ""))
    .map((values) => Object.fromEntries(headers.map((header, index) => [header, values[index] ?? ""])));
}

async function readCsv(name) {
  return parseCsv(await fs.readFile(path.join(inputDir, name), "utf8"));
}

function excelColumn(index) {
  let value = index + 1;
  let output = "";
  while (value > 0) {
    const remainder = (value - 1) % 26;
    output = String.fromCharCode(65 + remainder) + output;
    value = Math.floor((value - 1) / 26);
  }
  return output;
}

function toDate(value) {
  if (!value || value === "N/A") return value;
  const [year, month, day] = value.split("-").map(Number);
  return new Date(Date.UTC(year, month - 1, day));
}

function toNumber(value) {
  if (value === "") return null;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : value;
}

function writeMatrix(sheet, startRow, startCol, matrix) {
  if (!matrix.length || !matrix[0].length) return;
  sheet.getRangeByIndexes(startRow, startCol, matrix.length, matrix[0].length).values = matrix;
}

function styleTitle(sheet, range, title) {
  sheet.getRange(range).merge();
  sheet.getRange(range).values = [[title]];
  sheet.getRange(range).format = {
    fill: dark,
    font: { bold: true, color: "#FFFFFF", size: 20 },
    verticalAlignment: "center",
  };
  sheet.getRange(range).format.rowHeight = 34;
}

function styleHeaders(sheet, range) {
  sheet.getRange(range).format = {
    fill: teal,
    font: { bold: true, color: "#FFFFFF" },
    wrapText: true,
    verticalAlignment: "center",
    borders: { preset: "inside", style: "thin", color: "#D6E5E2" },
  };
  sheet.getRange(range).format.rowHeight = 30;
}

function styleDataSheet(sheet, title, headers, rows, options = {}) {
  const lastColumn = excelColumn(headers.length - 1);
  styleTitle(sheet, `A1:${lastColumn}1`, title);
  writeMatrix(sheet, 2, 0, [headers]);
  styleHeaders(sheet, `A3:${lastColumn}3`);
  writeMatrix(sheet, 3, 0, rows);
  sheet.showGridLines = false;
  sheet.freezePanes.freezeRows(3);
  sheet.getRange(`A4:${lastColumn}${rows.length + 3}`).format = {
    font: { size: 9, color: "#000000" },
    verticalAlignment: "top",
    borders: { insideHorizontal: { style: "thin", color: "#EEE8DE" } },
  };
  if (rows.length) {
    sheet.tables.add(`A3:${lastColumn}${rows.length + 3}`, true, options.tableName);
  }
  for (const [columnIndex, width] of Object.entries(options.widths ?? {})) {
    sheet.getRange(`${excelColumn(Number(columnIndex))}:${excelColumn(Number(columnIndex))}`).format.columnWidth = width;
  }
  for (const columnIndex of options.dateColumns ?? []) {
    sheet.getRange(`${excelColumn(columnIndex)}4:${excelColumn(columnIndex)}${rows.length + 3}`).format.numberFormat = dateFormat;
  }
  for (const columnIndex of options.currencyColumns ?? []) {
    sheet.getRange(`${excelColumn(columnIndex)}4:${excelColumn(columnIndex)}${rows.length + 3}`).format.numberFormat = currencyFormat;
  }
  for (const columnIndex of options.integerColumns ?? []) {
    sheet.getRange(`${excelColumn(columnIndex)}4:${excelColumn(columnIndex)}${rows.length + 3}`).format.numberFormat = integerFormat;
  }
  for (const columnIndex of options.wrapColumns ?? []) {
    sheet.getRange(`${excelColumn(columnIndex)}4:${excelColumn(columnIndex)}${rows.length + 3}`).format.wrapText = true;
  }
}

const [
  portfolio,
  units,
  leases,
  schedule,
  scanAssets,
  coverage,
  screens,
  fields,
  roleJourneys,
  crudLifecycles,
  notifications,
  financialEvents,
  journal,
  monthlyExpected,
] = await Promise.all([
  readCsv("portfolio.csv"),
  readCsv("units.csv"),
  readCsv("leases.csv"),
  readCsv("schedule.csv"),
  readCsv("scan-assets.csv"),
  readCsv("surface-coverage.csv"),
  readCsv("screen-inventory.csv"),
  readCsv("field-inventory.csv"),
  readCsv("role-journeys.csv"),
  readCsv("crud-lifecycles.csv"),
  readCsv("notification-scenarios.csv"),
  readCsv("financial-oracle.csv"),
  readCsv("journal.csv"),
  readCsv("monthly-controls.csv"),
]);
const controls = JSON.parse(await fs.readFile(path.join(inputDir, "control-totals.json"), "utf8"));

const workbook = Workbook.create();
workbook.comments.setSelf({ displayName: "User" });
const cover = workbook.worksheets.add("Cover");
const monthly = workbook.worksheets.add("Monthly Controls");
const checks = workbook.worksheets.add("Checks");
const portfolioSheet = workbook.worksheets.add("Portfolio");
const unitsSheet = workbook.worksheets.add("Units");
const leasesSheet = workbook.worksheets.add("Leases");
const scheduleSheet = workbook.worksheets.add("Schedule");
const scansSheet = workbook.worksheets.add("Scan Assets");
const coverageSheet = workbook.worksheets.add("Coverage");
const screensSheet = workbook.worksheets.add("Screens");
const fieldsSheet = workbook.worksheets.add("Fields");
const rolesSheet = workbook.worksheets.add("Role Journeys");
const crudSheet = workbook.worksheets.add("CRUD Lifecycles");
const notificationsSheet = workbook.worksheets.add("Notifications");
const financialSheet = workbook.worksheets.add("Financial Events");
const journalSheet = workbook.worksheets.add("Journal");

// Raw/imported sheets.
styleDataSheet(
  portfolioSheet,
  "Opening Portfolio — 30 Properties",
  [
    "Property ID", "Property Name", "Structure", "Address", "City", "State", "ZIP",
    "Owner ID", "Owner", "Ownership %", "Acquired", "Original Cost", "Land", "Building Basis",
    "Loan ID", "Opening Loan", "Monthly Principal", "Monthly Interest", "Monthly Escrow",
  ],
  portfolio.map((row) => [
    row.property_id, row.property_name, row.rental_structure, row.address, row.city, row.state,
    row.postal_code, row.owner_entity_id, row.owner_entity_name, Number(row.ownership_pct) / 100,
    toDate(row.acquisition_date), Number(row.original_cost_cents) / 100, Number(row.land_value_cents) / 100,
    Number(row.building_basis_cents) / 100, row.loan_id, Number(row.opening_loan_balance_cents) / 100,
    Number(row.monthly_principal_cents) / 100, Number(row.monthly_interest_cents) / 100,
    Number(row.monthly_escrow_cents || 0) / 100,
  ]),
  {
    tableName: "PortfolioTable",
    dateColumns: [10],
    currencyColumns: [11, 12, 13, 15, 16, 17, 18],
    widths: { 0: 12, 1: 24, 2: 15, 3: 26, 4: 14, 6: 10, 8: 30, 10: 13, 11: 16, 15: 16, 16: 18, 17: 18, 18: 16 },
  },
);
portfolioSheet.getRange(`J4:J${portfolio.length + 3}`).format.numberFormat = "0.0%";

styleDataSheet(
  unitsSheet,
  "Opening Units — 40 Rentable Spaces",
  ["Unit ID", "Property ID", "Label", "Bedrooms", "Bathrooms", "Square Feet", "Market Rent", "Opening State"],
  units.map((row) => [
    row.unit_id, row.property_id, row.label, Number(row.bedrooms), Number(row.bathrooms),
    Number(row.square_feet), Number(row.market_rent_cents) / 100, row.opening_state,
  ]),
  {
    tableName: "UnitsTable",
    currencyColumns: [6],
    integerColumns: [3, 5],
    widths: { 0: 12, 1: 12, 2: 12, 6: 16, 7: 16 },
  },
);

styleDataSheet(
  leasesSheet,
  "Lease Relationships — Opening and 2027 Changes",
  ["Lease ID", "Unit ID", "Tenant ID", "Tenant", "Email", "Start", "End", "Monthly Rent", "Deposit", "Origin", "Lifecycle Scenario"],
  leases.map((row) => [
    row.lease_id, row.unit_id, row.tenant_id, row.tenant_name, row.tenant_email,
    toDate(row.start_date), toDate(row.end_date), Number(row.monthly_rent_cents) / 100,
    Number(row.deposit_cents) / 100, row.origin, row.lifecycle_scenario,
  ]),
  {
    tableName: "LeasesTable",
    dateColumns: [5, 6],
    currencyColumns: [7, 8],
    wrapColumns: [10],
    widths: { 0: 12, 3: 22, 4: 28, 5: 13, 6: 13, 7: 15, 8: 15, 9: 24, 10: 38 },
  },
);

styleDataSheet(
  scheduleSheet,
  "Dated Execution Calendar — 2027",
  [
    "Run ID", "Simulated Clock", "Sequence", "Phase", "Role", "Client", "Entry Mode",
    "Workflow", "Entity Refs", "Asset Refs", "Financial Refs", "Exact Actions",
    "Expected Result", "Evidence Required", "Negative / Retry", "Completion Gate",
  ],
  schedule.map((row) => [
    row.run_id, toDate(row.simulated_clock), Number(row.sequence), row.phase, row.role, row.client,
    row.entry_mode, row.workflow, row.entity_refs, row.asset_refs, row.financial_refs, row.exact_actions,
    row.expected_result, row.evidence_required, row.negative_or_retry_check, row.completion_gate,
  ]),
  {
    tableName: "ScheduleTable",
    dateColumns: [1],
    integerColumns: [2],
    wrapColumns: [7, 8, 9, 10, 11, 12, 13, 14, 15],
    widths: { 0: 20, 1: 15, 3: 22, 4: 24, 5: 16, 6: 22, 7: 38, 8: 28, 9: 28, 10: 28, 11: 58, 12: 52, 13: 48, 14: 48, 15: 45 },
  },
);

styleDataSheet(
  scansSheet,
  "Synthetic Scan Corpus Manifest",
  [
    "Asset ID", "Planned Date", "Filename", "Document Family", "Intended Target",
    "Intake Expectation", "Format", "Pages / Images", "Capture Profile", "Property ID",
    "Unit ID", "Lease ID", "Financial Event", "Expected Fields", "Edge Case",
    "Paired Manual Action", "Confidentiality",
  ],
  scanAssets.map((row) => [
    row.asset_id, toDate(row.planned_date), row.filename, row.document_family, row.intended_target,
    row.intake_expectation, row.format, row.pages_or_images, row.capture_profile, row.property_id,
    row.unit_id, row.lease_id, row.financial_event_id, row.expected_fields, row.edge_case,
    row.paired_manual_action, row.confidentiality,
  ]),
  {
    tableName: "ScanAssetsTable",
    dateColumns: [1],
    wrapColumns: [5, 8, 13, 14, 15, 16],
    widths: { 0: 13, 1: 15, 2: 38, 3: 30, 4: 26, 5: 54, 6: 14, 7: 18, 8: 36, 13: 55, 14: 45, 15: 50, 16: 48 },
  },
);

styleDataSheet(
  coverageSheet,
  "Web / Mobile × Scan / Manual Coverage",
  [
    "Coverage ID", "Domain", "Surface", "Web Route", "Mobile Surface", "Scan / Attachment Path",
    "Manual Web", "Manual Mobile", "Scan Web", "Scan Mobile", "Mobile Gap Policy",
    "Acceptance Action", "Required Proof",
  ],
  coverage.map((row) => [
    row.coverage_id, row.domain, row.surface, row.web_route, row.mobile_surface,
    row.scan_or_attachment_path, toDate(row.manual_web_date), toDate(row.manual_mobile_date),
    toDate(row.scan_web_date), toDate(row.scan_mobile_date), row.mobile_gap_policy,
    row.acceptance_action, row.required_proof,
  ]),
  {
    tableName: "CoverageTable",
    dateColumns: [6, 7, 8, 9],
    wrapColumns: [2, 3, 4, 5, 10, 11, 12],
    widths: { 0: 15, 1: 18, 2: 46, 3: 36, 4: 38, 5: 28, 6: 15, 7: 15, 8: 15, 9: 15, 10: 44, 11: 58, 12: 54 },
  },
);

styleDataSheet(
  screensSheet,
  "Every Current Web and Mobile Screen",
  ["Screen ID", "Client", "Route / Surface", "Source File", "Primary Role", "Allowed Roles", "Planned Date", "Allowed Proof", "Denied Proof", "Device / Responsive Proof"],
  screens.map((row) => [
    row.screen_id, row.client, row.surface, row.source_file, row.primary_role, row.allowed_roles, toDate(row.planned_date),
    row.allowed_proof, row.denied_proof, row.responsive_or_device_proof,
  ]),
  {
    tableName: "ScreensTable",
    dateColumns: [6],
    wrapColumns: [2, 3, 5, 7, 8, 9],
    widths: { 0: 13, 1: 12, 2: 44, 3: 52, 4: 26, 5: 38, 6: 15, 7: 54, 8: 50, 9: 46 },
  },
);

styleDataSheet(
  fieldsSheet,
  "Source-Derived Field-by-Field Persistence Ledger",
  ["Field ID", "Client", "Screen / Component", "Source File", "Line", "Control", "Field Key", "Label / Hint", "Role", "Planned Date", "Value Set", "Required Proof"],
  fields.map((row) => [
    row.field_id, row.client, row.screen_or_component, row.source_file, Number(row.source_line),
    row.control_type, row.field_key, row.label_or_hint, row.role, toDate(row.planned_date),
    row.value_set, row.required_proof,
  ]),
  {
    tableName: "FieldsTable",
    dateColumns: [9],
    integerColumns: [4],
    wrapColumns: [2, 3, 6, 7, 10, 11],
    widths: { 0: 13, 1: 11, 2: 44, 3: 52, 4: 9, 5: 20, 6: 30, 7: 32, 8: 28, 9: 15, 10: 50, 11: 65 },
  },
);

styleDataSheet(
  rolesSheet,
  "Role-Permitted Screen Journeys",
  ["Journey ID", "Planned Date", "Persona", "Client", "Screen ID", "Surface", "Expected Access", "Actions", "Proof"],
  roleJourneys.map((row) => [
    row.journey_id, toDate(row.planned_date), row.persona, row.client, row.screen_id,
    row.surface, row.expected_access, row.actions, row.proof,
  ]),
  {
    tableName: "RoleJourneysTable",
    dateColumns: [1],
    wrapColumns: [5, 7, 8],
    widths: { 0: 14, 1: 15, 2: 28, 3: 12, 4: 13, 5: 46, 6: 18, 7: 62, 8: 58 },
  },
);

styleDataSheet(
  crudSheet,
  "Full Create, Edit, and Delete-Safe Lifecycles",
  ["Lifecycle ID", "Planned Date", "Entity", "Role", "Lifecycle", "Web Proof", "Mobile Proof", "Scan / Attachment", "Delete Policy", "Acceptance"],
  crudLifecycles.map((row) => [
    row.lifecycle_id, toDate(row.planned_date), row.entity, row.role, row.lifecycle,
    row.web_proof, row.mobile_proof, row.scan_or_attachment_proof, row.delete_policy, row.acceptance,
  ]),
  {
    tableName: "CrudLifecyclesTable",
    dateColumns: [1],
    wrapColumns: [4, 6, 7, 8, 9],
    widths: { 0: 15, 1: 15, 2: 28, 3: 28, 4: 70, 5: 16, 6: 28, 7: 40, 8: 70, 9: 62 },
  },
);

styleDataSheet(
  notificationsSheet,
  "Notification, Reminder, Routing, and Delivery Scenarios",
  ["Notification ID", "Planned Date", "Trigger", "Recipients", "Channels / Route", "Deep Link", "Preference Test", "Delivery Test", "Privacy Test"],
  notifications.map((row) => [
    row.notification_id, toDate(row.planned_date), row.trigger_event, row.recipients,
    row.channels_or_route, row.deep_link_destination, row.preference_test, row.delivery_test, row.privacy_test,
  ]),
  {
    tableName: "NotificationsTable",
    dateColumns: [1],
    wrapColumns: [2, 3, 4, 5, 6, 7, 8],
    widths: { 0: 16, 1: 15, 2: 38, 3: 30, 4: 32, 5: 30, 6: 56, 7: 66, 8: 58 },
  },
);

styleDataSheet(
  financialSheet,
  "Independent Financial Event Oracle",
  [
    "Event ID", "Entry Date", "Effective Date", "Event Type", "Category", "Property ID", "Unit ID",
    "Lease ID", "Counterparty", "Source Mode", "Source Asset", "Reference", "Amount",
    "AR Delta", "Operating Cash Delta", "Deposit Cash Delta", "Reserve Cash Delta",
    "Deposit Liability Delta", "Loan Liability Delta", "Income", "Expense", "Owner Equity Delta",
    "Owner Distribution", "Capital Asset Delta", "Accumulated Depreciation Delta", "Memo",
  ],
  financialEvents.map((row) => [
    row.event_id, toDate(row.entry_date), toDate(row.effective_date), row.event_type, row.category,
    row.property_id, row.unit_id, row.lease_id, row.counterparty, row.source_mode, row.source_asset_id,
    row.reference, Number(row.amount_cents) / 100, Number(row.ar_delta_cents) / 100,
    Number(row.operating_cash_delta_cents) / 100, Number(row.deposit_cash_delta_cents) / 100,
    Number(row.reserve_cash_delta_cents) / 100, Number(row.deposit_liability_delta_cents) / 100,
    Number(row.loan_liability_delta_cents) / 100, Number(row.income_cents) / 100,
    Number(row.expense_cents) / 100, Number(row.owner_equity_delta_cents) / 100,
    Number(row.owner_distribution_cents) / 100, Number(row.capital_asset_delta_cents) / 100,
    Number(row.accumulated_depreciation_delta_cents) / 100, row.memo,
  ]),
  {
    tableName: "FinancialEventsTable",
    dateColumns: [1, 2],
    currencyColumns: Array.from({ length: 13 }, (_, index) => index + 12),
    wrapColumns: [9, 25],
    widths: { 0: 14, 1: 14, 2: 14, 3: 24, 4: 28, 8: 30, 9: 32, 10: 16, 11: 28, 12: 16, 25: 48 },
  },
);

styleDataSheet(
  journalSheet,
  "Balanced Double-Entry Journal",
  ["Event ID", "Line", "Entry Date", "Effective Date", "Property ID", "Unit ID", "Lease ID", "Account", "Debit", "Credit", "Memo"],
  journal.map((row) => [
    row.event_id, Number(row.line_no), toDate(row.entry_date), toDate(row.effective_date),
    row.property_id, row.unit_id, row.lease_id, row.account, Number(row.debit_cents) / 100,
    Number(row.credit_cents) / 100, row.memo,
  ]),
  {
    tableName: "JournalTable",
    dateColumns: [2, 3],
    currencyColumns: [8, 9],
    integerColumns: [1],
    wrapColumns: [10],
    widths: { 0: 14, 1: 8, 2: 14, 3: 14, 7: 34, 8: 17, 9: 17, 10: 54 },
  },
);

// Monthly controls with formula-driven recalculation from Financial Events.
styleTitle(monthly, "A1:Q1", "Monthly Financial Controls — Expected vs Recalculated");
monthly.getRange("A2:Q2").merge();
monthly.getRange("A2:Q2").values = [[
  "Expected columns come from the frozen CSV oracle. Recalculated columns use SUMIFS against the Financial Events sheet by effective date. Every variance must be zero before the simulation clock advances.",
]];
monthly.getRange("A2:Q2").format = { fill: pale, font: { color: "#000000", italic: true }, wrapText: true };
monthly.getRange("A2:Q2").format.rowHeight = 34;
const monthlyHeaders = [
  "Month", "Expected Income", "Recalc Income", "Income Variance", "Expected Expense", "Recalc Expense",
  "Expense Variance", "Expected NOI", "Recalc NOI", "NOI Variance", "Expected Cash Δ", "Recalc Cash Δ",
  "Cash Variance", "Expected AR Δ", "Recalc AR Δ", "AR Variance", "Status",
];
writeMatrix(monthly, 4, 0, [monthlyHeaders]);
styleHeaders(monthly, "A5:Q5");
for (let index = 0; index < monthlyExpected.length; index += 1) {
  const rowNumber = index + 6;
  const monthStart = new Date(Date.UTC(2027, index, 1));
  const nextMonth = new Date(Date.UTC(index === 11 ? 2028 : 2027, index === 11 ? 0 : index + 1, 1));
  const expected = monthlyExpected[index];
  monthly.getRange(`A${rowNumber}:Q${rowNumber}`).values = [[
    monthStart,
    Number(expected.income_cents) / 100,
    null,
    null,
    Number(expected.expense_cents) / 100,
    null,
    null,
    Number(expected.net_operating_income_cents) / 100,
    null,
    null,
    Number(expected.operating_cash_delta_cents) / 100,
    null,
    null,
    Number(expected.ar_delta_cents) / 100,
    null,
    null,
    null,
  ]];
  monthly.getRange(`C${rowNumber}`).formulas = [[
    `=SUMIFS('Financial Events'!$T$4:$T$${financialEvents.length + 3},'Financial Events'!$C$4:$C$${financialEvents.length + 3},">="&A${rowNumber},'Financial Events'!$C$4:$C$${financialEvents.length + 3},"<"&DATE(${nextMonth.getUTCFullYear()},${nextMonth.getUTCMonth() + 1},1))`,
  ]];
  monthly.getRange(`D${rowNumber}`).formulas = [[`=C${rowNumber}-B${rowNumber}`]];
  monthly.getRange(`F${rowNumber}`).formulas = [[
    `=SUMIFS('Financial Events'!$U$4:$U$${financialEvents.length + 3},'Financial Events'!$C$4:$C$${financialEvents.length + 3},">="&A${rowNumber},'Financial Events'!$C$4:$C$${financialEvents.length + 3},"<"&DATE(${nextMonth.getUTCFullYear()},${nextMonth.getUTCMonth() + 1},1))`,
  ]];
  monthly.getRange(`G${rowNumber}`).formulas = [[`=F${rowNumber}-E${rowNumber}`]];
  monthly.getRange(`I${rowNumber}`).formulas = [[`=C${rowNumber}-F${rowNumber}`]];
  monthly.getRange(`J${rowNumber}`).formulas = [[`=I${rowNumber}-H${rowNumber}`]];
  monthly.getRange(`L${rowNumber}`).formulas = [[
    `=SUMIFS('Financial Events'!$O$4:$O$${financialEvents.length + 3},'Financial Events'!$C$4:$C$${financialEvents.length + 3},">="&A${rowNumber},'Financial Events'!$C$4:$C$${financialEvents.length + 3},"<"&DATE(${nextMonth.getUTCFullYear()},${nextMonth.getUTCMonth() + 1},1),'Financial Events'!$D$4:$D$${financialEvents.length + 3},"<>OpeningBalance")`,
  ]];
  monthly.getRange(`M${rowNumber}`).formulas = [[`=L${rowNumber}-K${rowNumber}`]];
  monthly.getRange(`O${rowNumber}`).formulas = [[
    `=SUMIFS('Financial Events'!$N$4:$N$${financialEvents.length + 3},'Financial Events'!$C$4:$C$${financialEvents.length + 3},">="&A${rowNumber},'Financial Events'!$C$4:$C$${financialEvents.length + 3},"<"&DATE(${nextMonth.getUTCFullYear()},${nextMonth.getUTCMonth() + 1},1))`,
  ]];
  monthly.getRange(`P${rowNumber}`).formulas = [[`=O${rowNumber}-N${rowNumber}`]];
  monthly.getRange(`Q${rowNumber}`).formulas = [[
    `=IF(AND(ABS(D${rowNumber})<0.01,ABS(G${rowNumber})<0.01,ABS(J${rowNumber})<0.01,ABS(M${rowNumber})<0.01,ABS(P${rowNumber})<0.01),"OK","REVIEW")`,
  ]];
}
monthly.getRange("A18:Q18").format = { borders: { top: { style: "double", color: dark } }, font: { bold: true } };
monthly.getRange("A18").values = [["Year total"]];
for (const column of ["B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P"]) {
  monthly.getRange(`${column}18`).formulas = [[`=SUM(${column}6:${column}17)`]];
}
monthly.getRange("Q18").formulas = [['=IF(COUNTIF(Q6:Q17,"<>OK")=0,"OK","REVIEW")']];
monthly.getRange("A6:A17").format.numberFormat = "mmm yyyy";
monthly.getRange("B6:P18").format.numberFormat = currencyFormat;
monthly.getRange("C6:Q18").format.font = { color: green };
monthly.getRange("A6:Q18").format.borders = { insideHorizontal: { style: "thin", color: "#EEE8DE" } };
monthly.getRange("Q6:Q18").conditionalFormats.add("containsText", { text: "OK", format: { fill: "#DFF2E5", font: { bold: true, color: "#1E6E3D" } } });
monthly.getRange("Q6:Q18").conditionalFormats.add("containsText", { text: "REVIEW", format: { fill: "#FBE1DD", font: { bold: true, color: red } } });
monthly.freezePanes.freezeRows(5);
monthly.showGridLines = false;
for (let index = 0; index < 17; index += 1) {
  monthly.getRange(`${excelColumn(index)}:${excelColumn(index)}`).format.columnWidth = index === 0 ? 14 : index === 16 ? 14 : 18;
}
// Formula-backed helper range and a single useful chart.
monthly.getRange("S5:V5").values = [["Month", "Income", "Expense", "NOI"]];
monthly.getRange("S5:V5").format = { fill: dark, font: { bold: true, color: "#FFFFFF" } };
for (let index = 0; index < 12; index += 1) {
  const row = index + 6;
  monthly.getRange(`S${row}:V${row}`).formulas = [[`=A${row}`, `=C${row}`, `=F${row}`, `=I${row}`]];
}
monthly.getRange("S6:S17").format.numberFormat = "mmm";
monthly.getRange("T6:V17").format.numberFormat = currencyFormat;
const chart = monthly.charts.add("line", monthly.getRange("S5:V17"));
chart.title = "2027 Income, Expense, and Net Operating Result";
chart.hasLegend = true;
chart.xAxis = { axisType: "textAxis" };
chart.yAxis = { numberFormatCode: "$#,##0" };
chart.setPosition("S19", "AD36");

// Checks.
styleTitle(checks, "A1:G1", "Model Checks");
checks.getRange("A2:G2").merge();
checks.getRange("A2:G2").values = [[
  "Each row is a separate assertion. Actual values are formulas linked to source sheets; expected values are the frozen control totals generated before workbook creation.",
]];
checks.getRange("A2:G2").format = { fill: pale, font: { italic: true }, wrapText: true };
writeMatrix(checks, 4, 0, [["Check", "Actual", "Expected", "Difference", "Tolerance", "Status", "Fix Hint"]]);
styleHeaders(checks, "A5:G5");
const checkRows = [
  ["Journal debits equal credits", `=SUM('Journal'!$I$4:$I$${journal.length + 3})`, `=SUM('Journal'!$J$4:$J$${journal.length + 3})`, "Journal"],
  ["Monthly income totals match", "=SUM('Monthly Controls'!$C$6:$C$17)", controls.year_income_cents / 100, "Monthly Controls / Financial Events"],
  ["Monthly expense totals match", "=SUM('Monthly Controls'!$F$6:$F$17)", controls.year_expense_cents / 100, "Monthly Controls / Financial Events"],
  ["Year NOI matches", "=SUM('Monthly Controls'!$I$6:$I$17)", controls.year_noi_cents / 100, "Monthly Controls"],
  ["Property count", `=COUNTA('Portfolio'!$A$4:$A$${portfolio.length + 3})`, controls.properties, "Portfolio"],
  ["Unit count", `=COUNTA('Units'!$A$4:$A$${units.length + 3})`, controls.units, "Units"],
  ["Opening occupied count", `=COUNTIF('Units'!$H$4:$H$${units.length + 3},"Occupied")`, controls.opening_occupied, "Units"],
  ["Opening vacant count", `=COUNTIF('Units'!$H$4:$H$${units.length + 3},"Vacant")`, controls.opening_vacant, "Units"],
  ["Lease relationship count", `=COUNTA('Leases'!$A$4:$A$${leases.length + 3})`, controls.leases, "Leases"],
  ["Dated run row count", `=COUNTA('Schedule'!$A$4:$A$${schedule.length + 3})`, controls.runbook_rows, "Schedule"],
  ["Scan asset count", `=COUNTA('Scan Assets'!$A$4:$A$${scanAssets.length + 3})`, controls.scan_assets, "Scan Assets"],
  ["Coverage surface count", `=COUNTA('Coverage'!$A$4:$A$${coverage.length + 3})`, controls.coverage_surfaces, "Coverage"],
  ["Screen inventory count", `=COUNTA('Screens'!$A$4:$A$${screens.length + 3})`, controls.inventoried_screens, "Screens"],
  ["Field inventory count", `=COUNTA('Fields'!$A$4:$A$${fields.length + 3})`, controls.inventoried_fields, "Fields"],
  ["Role journey count", `=COUNTA('Role Journeys'!$A$4:$A$${roleJourneys.length + 3})`, controls.role_journeys, "Role Journeys"],
  ["CRUD lifecycle count", `=COUNTA('CRUD Lifecycles'!$A$4:$A$${crudLifecycles.length + 3})`, controls.crud_lifecycles, "CRUD Lifecycles"],
  ["Notification scenario count", `=COUNTA('Notifications'!$A$4:$A$${notifications.length + 3})`, controls.notification_scenarios, "Notifications"],
  ["Financial event count", `=COUNTA('Financial Events'!$A$4:$A$${financialEvents.length + 3})`, controls.financial_events, "Financial Events"],
  ["Journal line count", `=COUNTA('Journal'!$A$4:$A$${journal.length + 3})`, controls.journal_lines, "Journal"],
  ["All monthly statuses OK", '=COUNTIF(\'Monthly Controls\'!$Q$6:$Q$18,"<>OK")', 0, "Monthly Controls"],
];
checkRows.forEach(([label, actualFormula, expected, hint], index) => {
  const row = index + 6;
  checks.getRange(`A${row}:G${row}`).values = [[label, null, expected, null, 0.01, null, hint]];
  checks.getRange(`B${row}`).formulas = [[actualFormula]];
  checks.getRange(`D${row}`).formulas = [[`=B${row}-C${row}`]];
  checks.getRange(`F${row}`).formulas = [[`=IF(ABS(D${row})<=E${row},"OK","REVIEW")`]];
});
const checkEndRow = checkRows.length + 5;
checks.getRange(`B6:F${checkEndRow}`).format.numberFormat = currencyFormat;
checks.getRange(`B10:F${checkEndRow}`).format.numberFormat = integerFormat;
checks.getRange(`B6:B${checkEndRow}`).format.font = { color: green };
checks.getRange(`D6:F${checkEndRow}`).format.font = { color: "#000000" };
checks.getRange(`F6:F${checkEndRow}`).conditionalFormats.add("containsText", { text: "OK", format: { fill: "#DFF2E5", font: { bold: true, color: "#1E6E3D" } } });
checks.getRange(`F6:F${checkEndRow}`).conditionalFormats.add("containsText", { text: "REVIEW", format: { fill: "#FBE1DD", font: { bold: true, color: red } } });
checks.getRange(`A6:G${checkEndRow}`).format.borders = { insideHorizontal: { style: "thin", color: "#EEE8DE" } };
checks.getRange("A:A").format.columnWidth = 34;
checks.getRange("B:F").format.columnWidth = 18;
checks.getRange("G:G").format.columnWidth = 36;
checks.showGridLines = false;
checks.freezePanes.freezeRows(5);

// Cover / operating instructions.
styleTitle(cover, "A1:J1", "Rental Command — 2027 Scan-First Simulation Oracle");
cover.getRange("A3:J3").merge();
cover.getRange("A3:J3").values = [[
  "Purpose: execute one synthetic rental-company year day by day, prove scan-first intake and manual parity on web/mobile, and reconcile every financial event against an independent double-entry oracle.",
]];
cover.getRange("A3:J3").format = { fill: pale, wrapText: true, font: { size: 12 } };
cover.getRange("A3:J3").format.rowHeight = 42;
cover.getRange("A5:B5").values = [["Model status", null]];
cover.getRange("B5").formulas = [[`=IF(COUNTIF('Checks'!$F$6:$F$${checkEndRow},"<>OK")=0,"OK","REVIEW")`]];
cover.getRange("A5:B5").format = { fill: dark, font: { bold: true, color: "#FFFFFF" } };
cover.getRange("B5").format.font = { bold: true, color: "#FFFFFF", size: 14 };
cover.getRange("B5").conditionalFormats.add("containsText", { text: "OK", format: { fill: "#1E6E3D", font: { bold: true, color: "#FFFFFF" } } });
cover.getRange("B5").conditionalFormats.add("containsText", { text: "REVIEW", format: { fill: red, font: { bold: true, color: "#FFFFFF" } } });

const kpis = [
  ["Properties", controls.properties, "Units", controls.units],
  ["Opening occupied", controls.opening_occupied, "Opening vacant", controls.opening_vacant],
  ["Planned scans", controls.scan_assets, "Dated run rows", controls.runbook_rows],
  ["Current screens", controls.inventoried_screens, "Inventoried fields", controls.inventoried_fields],
  ["Role journeys", controls.role_journeys, "Notification scenarios", controls.notification_scenarios],
  ["Financial events", controls.financial_events, "Coverage surfaces", controls.coverage_surfaces],
];
writeMatrix(cover, 6, 0, kpis);
cover.getRange("A7:D12").format = { fill: "#FFFFFF", borders: { preset: "outside", style: "thin", color: line } };
cover.getRange("A7:A12").format.font = { bold: true, color: dark };
cover.getRange("C7:C12").format.font = { bold: true, color: dark };
cover.getRange("B7:B12").format.numberFormat = integerFormat;
cover.getRange("D7:D12").format.numberFormat = integerFormat;

cover.getRange("F5:J5").merge();
cover.getRange("F5:J5").values = [["Year financial controls"]];
cover.getRange("F5:J5").format = { fill: teal, font: { bold: true, color: "#FFFFFF" } };
writeMatrix(cover, 6, 5, [
  ["Income", null, "Expense", null, "NOI"],
  [null, null, null, null, null],
  ["Journal debits", null, "Journal credits", null, "Difference"],
  [null, null, null, null, null],
]);
cover.getRange("F7:J7").format.font = { bold: true, color: dark };
cover.getRange("F9:J9").format.font = { bold: true, color: dark };
cover.getRange("F8").formulas = [["='Checks'!B7"]];
cover.getRange("H8").formulas = [["='Checks'!B8"]];
cover.getRange("J8").formulas = [["='Checks'!B9"]];
cover.getRange("F10").formulas = [["='Checks'!B6"]];
cover.getRange("H10").formulas = [["='Checks'!C6"]];
cover.getRange("J10").formulas = [["='Checks'!D6"]];
cover.getRange("F8:J10").format.numberFormat = currencyFormat;
cover.getRange("F8:J10").format.font = { color: green, bold: true };

cover.getRange("A14:J14").merge();
cover.getRange("A14:J14").values = [["Operating instructions"]];
cover.getRange("A14:J14").format = { fill: dark, font: { bold: true, color: "#FFFFFF" } };
const instructions = [
  ["1", "Open index.html and select the next RUN row for the simulated date."],
  ["2", "Set the app clock and America/New_York timezone; capture baseline counts, balances, role, and build/SHA."],
  ["3", "Create only the listed synthetic scan asset. Review the draft; never allow extraction to save before confirmation."],
  ["4", "Execute the manual web/mobile pass on the listed distinct record; do not duplicate the financial event for parity."],
  ["5", "Compare every FIN reference across tenant, Unit, Lease, Accounting, banking, deposits, owner, portal, and reports."],
  ["6", "Close the day only when evidence exists and all applicable Monthly Controls/Checks remain OK."],
];
writeMatrix(cover, 14, 0, instructions);
cover.getRange("A15:A20").format = { fill: gold, font: { bold: true, color: "#FFFFFF" }, horizontalAlignment: "center" };
cover.getRange("B15:J20").merge(true);
for (let row = 15; row <= 20; row += 1) {
  cover.getRange(`B${row}:J${row}`).format = { wrapText: true, borders: { bottom: { style: "thin", color: line } } };
  cover.getRange(`B${row}:J${row}`).format.rowHeight = 32;
}
cover.getRange("A22:J22").merge();
cover.getRange("A22:J22").values = [[
  "Legend — Blue text: editable operator inputs (none in frozen oracle). Green text: cross-sheet linked formulas. Black text: calculations and frozen source data. Yellow: attention. Red: failed check.",
]];
cover.getRange("A22:J22").format = { fill: "#FFF4CC", wrapText: true, font: { color: "#000000", italic: true } };
cover.getRange("A22:J22").format.rowHeight = 36;
cover.getRange("A24:J27").merge();
cover.getRange("A24:J27").values = [[
  "Source and limitations\nThis workbook is generated from the TSK-749 portfolio, calendar, scan manifest, financial-event oracle, and double-entry journal in Docs/Testing/YearSimulation2027. All names, properties, account references, documents, and signatures are synthetic QA data. This is a product-acceptance control, not legal, tax, accounting, banking, or property-management advice.",
]];
cover.getRange("A24:J27").format = { fill: pale, wrapText: true, verticalAlignment: "top", font: { color: "#5B6875" } };
cover.getRange("A:A").format.columnWidth = 20;
cover.getRange("B:B").format.columnWidth = 16;
cover.getRange("C:C").format.columnWidth = 20;
cover.getRange("D:D").format.columnWidth = 16;
cover.getRange("E:E").format.columnWidth = 4;
cover.getRange("F:J").format.columnWidth = 18;
cover.showGridLines = false;
cover.freezePanes.freezeRows(1);

// Attach source comments to key control cells.
workbook.comments.addThread({ cell: cover.getRange("F8") }, "Source: financial-oracle.csv, recalculated on the Monthly Controls sheet.");
workbook.comments.addThread({ cell: cover.getRange("F10") }, "Source: journal.csv. Debits and credits must tie exactly.");
workbook.comments.addThread({ cell: cover.getRange("B5") }, "Model status is formula-driven from the Checks sheet.");

// Verification.
const inspectCover = await workbook.inspect({
  kind: "table",
  range: "Cover!A1:J27",
  include: "values,formulas",
  tableMaxRows: 30,
  tableMaxCols: 12,
});
console.log(inspectCover.ndjson);
const inspectChecks = await workbook.inspect({
  kind: "table",
  range: `Checks!A1:G${checkEndRow}`,
  include: "values,formulas",
  tableMaxRows: 25,
  tableMaxCols: 10,
});
console.log(inspectChecks.ndjson);
const inspectMonthly = await workbook.inspect({
  kind: "table",
  range: "Monthly Controls!A5:Q18",
  include: "values,formulas",
  tableMaxRows: 20,
  tableMaxCols: 20,
});
console.log(inspectMonthly.ndjson);
const errors = await workbook.inspect({
  kind: "match",
  searchTerm: "#REF!|#DIV/0!|#VALUE!|#NAME\\?|#N/A",
  options: { useRegex: true, maxResults: 300 },
  summary: "final formula error scan",
});
console.log(errors.ndjson);

await fs.mkdir(primaryOutputDir, { recursive: true });
await fs.mkdir(repoOutputDir, { recursive: true });
const previewDir = path.join(primaryOutputDir, "previews");
await fs.mkdir(previewDir, { recursive: true });
const previewRanges = {
  Cover: "A1:J27",
  "Monthly Controls": "A1:Q18",
  Checks: `A1:G${checkEndRow}`,
  Portfolio: "A1:R18",
  Units: "A1:H20",
  Leases: "A1:K18",
  Schedule: "A1:P14",
  "Scan Assets": "A1:Q14",
  Coverage: "A1:M18",
  Screens: "A1:J18",
  Fields: "A1:L18",
  "Role Journeys": "A1:I18",
  "CRUD Lifecycles": "A1:J18",
  Notifications: "A1:I18",
  "Financial Events": "A1:Z14",
  Journal: "A1:K16",
};
for (const [sheetName, range] of Object.entries(previewRanges)) {
  const preview = await workbook.render({ sheetName, range, scale: 1, format: "png" });
  const filename = sheetName.toLowerCase().replaceAll(" ", "-") + ".png";
  await fs.writeFile(path.join(previewDir, filename), new Uint8Array(await preview.arrayBuffer()));
}

const output = await SpreadsheetFile.exportXlsx(workbook);
const filename = "rental-command-2027-simulation-oracle.xlsx";
await output.save(path.join(primaryOutputDir, filename));
await output.save(path.join(repoOutputDir, filename));
console.log(JSON.stringify({
  workbook: path.join(repoOutputDir, filename),
  primary: path.join(primaryOutputDir, filename),
  previews: previewDir,
  sheets: previewRanges,
}));
