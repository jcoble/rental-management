#!/usr/bin/env node
// Derives per-report EXPECTED figures from the ground-truth event log, using each app report's
// EXACT predicate (design §6.2 / §8.3-8.6, all source-verified). Writes expected/*.json and
// self-checks the three deliberate report divergences per property. This is what the Phase-4
// replay reconciles the app's API responses against.
//
// Usage: node tools/derive-expected.mjs   (paths relative to e2e/corpus/)

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve, join } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const CORPUS = resolve(HERE, "..");
const sc = JSON.parse(readFileSync(join(CORPUS, "scenario/scenario.json"), "utf8"));

function readCsv(p) {
  const lines = readFileSync(join(CORPUS, p), "utf8").trim().split("\n");
  const cols = splitCsv(lines[0]);
  return lines.slice(1).map(l => { const c = splitCsv(l); return Object.fromEntries(cols.map((k, i) => [k, c[i]])); });
}
function splitCsv(line) { // handles quoted fields
  const out = []; let cur = "", q = false;
  for (let i = 0; i < line.length; i++) { const ch = line[i];
    if (q) { if (ch === '"' && line[i + 1] === '"') { cur += '"'; i++; } else if (ch === '"') q = false; else cur += ch; }
    else { if (ch === '"') q = true; else if (ch === ",") { out.push(cur); cur = ""; } else cur += ch; } }
  out.push(cur); return out;
}
const r2 = (n) => Math.round((n + Number.EPSILON) * 100) / 100;
const num = (v) => v === "" || v == null ? 0 : Number(v);
const yr = (d) => d ? d.slice(0, 4) : "";

const events = readCsv("ledger/events.csv");
const deprRows = readCsv("ledger/depreciation.csv");
const depr = Object.fromEntries(deprRows.map(d => [d.property_code, d]));
const propOwner = Object.fromEntries(sc.properties.map(p => [p.code, p.owner]));
const propFee = Object.fromEntries(sc.properties.map(p => [p.code, p.feePercent]));
const ownerName = Object.fromEntries(sc.owners.map(o => [o.code, o.name]));
const PROPS = sc.properties.map(p => p.code);

// ── predicates ──────────────────────────────────────────────────────────────────────────────
const incomeAmt = (e) => e.status === "Paid" ? num(e.amount) : e.status === "Partial" ? num(e.amount_paid) : 0;
const isPay = (e, types) => e.record_kind === "Payment" && types.includes(e.payment_type);
const isExp = (e) => e.record_kind === "Expense";
const isLoan = (e) => e.record_kind === "LoanPayment";
const cashDateExp = (e) => e.paid_date || e.incurred_at;

function scheduleE(prop, y) {
  const inc = events.filter(e => e.property_code === prop && isPay(e, ["Rent", "LateFee", "Utility"]) &&
    ["Paid", "Partial"].includes(e.status) && yr(e.paid_date) === y).reduce((s, e) => s + incomeAmt(e), 0);
  const byCat = {};
  for (const e of events.filter(e => e.property_code === prop && isExp(e) && yr(e.incurred_at) === y))
    byCat[e.sched_e_category] = r2((byCat[e.sched_e_category] || 0) + num(e.amount));
  const interest = events.filter(e => e.property_code === prop && isLoan(e) && yr(e.due_date) === y).reduce((s, e) => s + num(e.loan_interest), 0);
  const dep = num(depr[prop]?.[`dep_${y}`]);
  const deductible = r2(Object.values(byCat).reduce((a, b) => a + b, 0));
  const totalExp = r2(deductible + interest + dep);
  return { income: r2(inc), deductibleByCategory: byCat, deductibleExpenses: deductible,
    mortgageInterest: r2(interest), depreciation: r2(dep), totalExpenses: totalExp, netIncome: r2(inc - totalExp) };
}
function cashflowPnl(prop, y) {
  const inc = events.filter(e => e.property_code === prop && isPay(e, ["Rent", "LateFee"]) &&
    ["Paid", "Partial"].includes(e.status) && yr(e.paid_date || e.due_date) === y).reduce((s, e) => s + incomeAmt(e), 0);
  const exp = events.filter(e => e.property_code === prop && isExp(e) && yr(cashDateExp(e)) === y).reduce((s, e) => s + num(e.amount), 0);
  return { income: r2(inc), expense: r2(exp), net: r2(inc - exp) };
}
function trueCashFlow(prop, y) {
  const inc = events.filter(e => e.property_code === prop && isPay(e, ["Rent", "LateFee"]) &&
    ["Paid", "Partial"].includes(e.status) && yr(e.paid_date || e.due_date) === y).reduce((s, e) => s + incomeAmt(e), 0);
  const opex = events.filter(e => e.property_code === prop && isExp(e) && e.escrow_covered !== "TRUE" && yr(cashDateExp(e)) === y).reduce((s, e) => s + num(e.amount), 0);
  const debt = events.filter(e => e.property_code === prop && isLoan(e) && yr(e.due_date) === y).reduce((s, e) => s + num(e.amount), 0);
  return { income: r2(inc), operatingExpenses: r2(opex), noi: r2(inc - opex), debtService: r2(debt), cashFlow: r2(inc - opex - debt) };
}
function propertyPnl(prop, y) {
  const inc = events.filter(e => e.property_code === prop && e.record_kind === "Payment" && e.status === "Paid" &&
    yr(e.paid_date || e.due_date) === y).reduce((s, e) => s + num(e.amount), 0);
  const exp = events.filter(e => e.property_code === prop && isExp(e) && yr(cashDateExp(e)) === y).reduce((s, e) => s + num(e.amount), 0);
  return { income: r2(inc), expense: r2(exp), net: r2(inc - exp) };
}

// ── per-property CY2025 + divergence self-checks ─────────────────────────────────────────────
const Y = "2025";
const byProp = {}; const checks = []; let anyFail = false;
for (const prop of PROPS) {
  const se = scheduleE(prop, Y), cf = cashflowPnl(prop, Y), tcf = trueCashFlow(prop, Y), pnl = propertyPnl(prop, Y);
  byProp[prop] = { owner: propOwner[prop], scheduleE: se, cashflowPnl: cf, trueCashFlow: tcf, propertyPnl: pnl };
  // DIV1: pnl.net - se.net == (P&L-only income) + interest + depreciation.
  // "otherInc" = income P&L counts but Schedule E doesn't (Other/SecurityDeposit payment types,
  // e.g. the NSF fee) — a real 4th divergence, asserted not bug-flagged.
  const otherInc = r2(pnl.income - se.income);
  const d1 = r2(pnl.net - se.netIncome), d1e = r2(otherInc + se.mortgageInterest + se.depreciation), ok1 = d1 === d1e;
  // DIV2: pnl.expense - tcf.opex == escrowed tax+ins
  const escrowed = events.filter(e => e.property_code === prop && isExp(e) && e.escrow_covered === "TRUE" && yr(cashDateExp(e)) === Y).reduce((s, e) => s + num(e.amount), 0);
  const d2 = r2(pnl.expense - tcf.operatingExpenses), ok2 = d2 === r2(escrowed);
  // DIV3: tcf.cashFlow - se.net == (depr + escrowedExp) - (principal + escrowCash)
  const principal = events.filter(e => e.property_code === prop && isLoan(e) && yr(e.due_date) === Y).reduce((s, e) => s + num(e.loan_principal), 0);
  const escrowCash = events.filter(e => e.property_code === prop && isLoan(e) && yr(e.due_date) === Y).reduce((s, e) => s + num(e.loan_escrow), 0);
  const d3 = r2(tcf.cashFlow - se.netIncome), d3e = r2((se.depreciation + escrowed) - (principal + escrowCash)), ok3 = d3 === d3e;
  if (!(ok1 && ok2 && ok3)) anyFail = true;
  checks.push({ prop, DIV1: { delta: d1, expect: d1e, ok: ok1, nonScheduleEIncome: otherInc }, DIV2: { delta: d2, expect: r2(escrowed), ok: ok2 }, DIV3: { delta: d3, expect: d3e, ok: ok3 } });
}

// ── portfolio totals (sum per each report's def) ─────────────────────────────────────────────
const sum = (fn) => PROPS.reduce((acc, p) => { const r = fn(p); for (const k in r) if (typeof r[k] === "number") acc[k] = r2((acc[k] || 0) + r[k]); return acc; }, {});
const portfolio = {
  scheduleE: sum(p => { const s = scheduleE(p, Y); return { income: s.income, deductibleExpenses: s.deductibleExpenses, mortgageInterest: s.mortgageInterest, depreciation: s.depreciation, totalExpenses: s.totalExpenses, netIncome: s.netIncome }; }),
  cashflowPnl: sum(p => cashflowPnl(p, Y)), trueCashFlow: sum(p => trueCashFlow(p, Y)), propertyPnl: sum(p => propertyPnl(p, Y)),
};

// ── owner statements 2025 (income Rent-Paid; expenses Paid; fee per-property; net) ───────────
const ownerStatements = {};
for (const o of sc.owners) {
  const props = sc.properties.filter(p => p.owner === o.code).map(p => p.code);
  let income = 0, expenses = 0, fee = 0;
  const lines = [];
  for (const prop of props) {
    const pi = events.filter(e => e.property_code === prop && e.record_kind === "Payment" && e.payment_type === "Rent" && e.status === "Paid" && yr(e.paid_date) === Y).reduce((s, e) => s + num(e.amount), 0);
    const pe = events.filter(e => e.property_code === prop && isExp(e) && e.status === "Paid" && yr(cashDateExp(e)) === Y).reduce((s, e) => s + num(e.amount), 0);
    const pf = r2(pi * propFee[prop] / 100);
    lines.push({ property: prop, income: r2(pi), expenses: r2(pe), managementFee: pf, net: r2(pi - pe - pf) });
    income = r2(income + r2(pi)); expenses = r2(expenses + r2(pe)); fee = r2(fee + pf);
  }
  ownerStatements[o.code] = { owner: ownerName[o.code], feePercent: o.feePercent, properties: lines, income, expenses, managementFee: fee, netToOwner: r2(income - expenses - fee) };
}

// ── security deposit register ────────────────────────────────────────────────────────────────
const deposits = {};
for (const L of sc.leases) {
  const held = events.filter(e => e.lease_code === L.code && e.record_kind === "SDHolding").reduce((s, e) => s + num(e.amount), 0);
  const ded = events.filter(e => e.lease_code === L.code && e.record_kind === "SDDeduction").reduce((s, e) => s + num(e.amount), 0);
  const ret = events.filter(e => e.lease_code === L.code && e.record_kind === "SDReturn").reduce((s, e) => s + num(e.amount), 0);
  if (held) deposits[L.code] = { property: L.property, held: r2(held), deductions: r2(ded), returned: r2(ret), balance: r2(Math.max(0, held - ded - ret)) };
}
const depositTotals = Object.values(deposits).reduce((a, d) => ({ held: r2(a.held + d.held), deductions: r2(a.deductions + d.deductions), returned: r2(a.returned + d.returned), balance: r2(a.balance + d.balance) }), { held: 0, deductions: 0, returned: 0, balance: 0 });

// ── delinquency at 2025-12-31 ────────────────────────────────────────────────────────────────
const asOf = "2025-12-31", cut30 = "2025-12-01";
const owed = events.filter(e => e.record_kind === "Payment" && ["Rent", "LateFee"].includes(e.payment_type) &&
  ["Scheduled", "Partial", "Late"].includes(e.status) && (e.status === "Late" || e.due_date < asOf));
const delinq = {};
for (const e of owed) {
  const amt = e.status === "Partial" ? r2(num(e.amount) - num(e.amount_paid)) : num(e.amount);
  const k = e.lease_code;
  delinq[k] = delinq[k] || { property: e.property_code, total: 0, current: 0, d31_60: 0, d61_90: 0, over90: 0 };
  delinq[k].total = r2(delinq[k].total + amt);
  const b = e.due_date >= cut30 ? "current" : e.due_date >= "2025-11-01" ? "d31_60" : e.due_date >= "2025-10-01" ? "d61_90" : "over90";
  delinq[k][b] = r2(delinq[k][b] + amt);
}
const delinqTotal = Object.values(delinq).reduce((a, d) => r2(a + d.total), 0);

// ── vendor 1099 (2025, >= $600) ──────────────────────────────────────────────────────────────
const vendors = {};
for (const e of events.filter(e => isExp(e) && yr(cashDateExp(e)) === Y && e.vendor)) vendors[e.vendor] = r2((vendors[e.vendor] || 0) + num(e.amount));
const vendor1099 = Object.entries(vendors).filter(([, v]) => v >= 600).map(([vendor, total]) => ({ vendor, totalPaid: total, needs1099: true })).sort((a, b) => b.totalPaid - a.totalPaid);

// ── spot-check history years ─────────────────────────────────────────────────────────────────
const spotcheck = {};
for (const y of ["2023", "2024"]) spotcheck[y] = {
  scheduleE: sum(p => { const s = scheduleE(p, y); return { income: s.income, totalExpenses: s.totalExpenses, netIncome: s.netIncome }; }),
  note: "history year — spot-check only (held-flat rent; not cent-exact)",
};

// ── write ────────────────────────────────────────────────────────────────────────────────────
const meta = { generated: "derive-expected.mjs", basis: "ledger/events.csv + emit-finance CSVs", spine: "CY2025 cent-exact", asOf };
writeFileSync(join(CORPUS, "expected/cy2025-by-property.json"), JSON.stringify({ meta, byProperty: byProp, divergenceChecks: checks }, null, 2) + "\n");
writeFileSync(join(CORPUS, "expected/cy2025-portfolio.json"), JSON.stringify({ meta, portfolio, ownerStatements, deposits: { byLease: deposits, totals: depositTotals }, delinquencyAtYearEnd: { asOf, byLease: delinq, total: delinqTotal }, vendor1099 }, null, 2) + "\n");
writeFileSync(join(CORPUS, "expected/spotcheck-2023-2024.json"), JSON.stringify({ meta, spotcheck }, null, 2) + "\n");

// ── report ───────────────────────────────────────────────────────────────────────────────────
console.log(`expected/*.json written. Divergence self-check across ${PROPS.length} properties:`);
console.log(`  ${anyFail ? "*** SOME FAILED ***" : "ALL PASS (DIV1/DIV2/DIV3 tie to the cent)"}`);
for (const c of checks) if (!(c.DIV1.ok && c.DIV2.ok && c.DIV3.ok))
  console.log(`  FAIL ${c.prop}:`, JSON.stringify(c));
console.log(`\nPortfolio CY2025:`);
console.log(`  Schedule E   income ${portfolio.scheduleE.income}  net ${portfolio.scheduleE.netIncome}  (interest ${portfolio.scheduleE.mortgageInterest}, depr ${portfolio.scheduleE.depreciation})`);
console.log(`  True cashflow NOI ${portfolio.trueCashFlow.noi}  debtSvc ${portfolio.trueCashFlow.debtService}  cashFlow ${portfolio.trueCashFlow.cashFlow}`);
console.log(`  Property P&L net ${portfolio.propertyPnl.net}`);
console.log(`  Owner net: ` + Object.entries(ownerStatements).map(([k, v]) => `${k}=${v.netToOwner}`).join("  "));
console.log(`  Deposits held ${depositTotals.held} balance ${depositTotals.balance}  ·  Delinquency@YE ${delinqTotal}  ·  1099 vendors ${vendor1099.length}`);
console.log(`  P01 Schedule E net ${byProp.P01.scheduleE.netIncome}  (income ${byProp.P01.scheduleE.income}, totalExp ${byProp.P01.scheduleE.totalExpenses})`);
