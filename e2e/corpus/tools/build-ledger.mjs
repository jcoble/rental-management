#!/usr/bin/env node
// Deterministic ground-truth ledger builder for the E2E corpus. Reads the frozen scenario +
// authoritative loan/depreciation CSVs (from tools/emit-finance) and emits ledger/events.csv:
// one row per money event (Payment / Expense / LoanPayment / SecurityDeposit) for CY2023–2025,
// with per-lease running balances. CY2025 is the cent-exact reconciliation spine; CY2023–24 is
// held-flat history for spot-checking (documented simplification: current tenant occupies from
// max(2023-01, acquisition) at base rent; prior-tenant turnovers within history are not modeled).
//
// Usage: node tools/build-ledger.mjs   (paths relative to e2e/corpus/)

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve, join } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const CORPUS = resolve(HERE, "..");
const sc = JSON.parse(readFileSync(join(CORPUS, "scenario/scenario.json"), "utf8"));

// ── helpers ──────────────────────────────────────────────────────────────────────────────────
const r2 = (n) => Math.round((n + Number.EPSILON) * 100) / 100;            // half-up to cents
const money = (n) => (n === "" || n == null) ? "" : r2(n).toFixed(2);
const pad = (n) => String(n).padStart(2, "0");
const iso = (y, m, d) => `${y}-${pad(m)}-${pad(d)}`;
const ym = (y, m) => `${y}-${pad(m)}`;
const dim = (y, m) => new Date(y, m, 0).getDate();                          // days in month
const clampDay = (y, m, day) => iso(y, m, Math.min(day, dim(y, m)));
const parseYMD = (s) => { const [y, m, d] = s.split("-").map(Number); return { y, m, d }; };
// iterate {y,m} inclusive from (y1,m1) to (y2,m2)
function* months(y1, m1, y2, m2) { let y = y1, m = m1; while (y < y2 || (y === y2 && m <= m2)) { yield { y, m }; m++; if (m > 12) { m = 1; y++; } } }
const geYM = (a, b) => a.y > b.y || (a.y === b.y && a.m >= b.m);

const WIN_START = { y: 2023, m: 1 };            // history window start
const WIN = { y: 2025, m: 12 };                 // corpus end
const owners = Object.fromEntries(sc.properties.map(p => [p.code, p.owner]));
const propByCode = Object.fromEntries(sc.properties.map(p => [p.code, p]));

const events = [];
let seq = 0;
function ev(o) {
  events.push({
    event_id: "", sim_entered_date: o.date, property_code: o.prop ?? "", unit_code: o.unit ?? "",
    lease_code: o.lease ?? "", owner_entity: o.owner ?? (o.prop ? owners[o.prop] : ""),
    record_kind: o.kind, payment_type: o.ptype ?? "", status: o.status ?? "", method: o.method ?? "",
    sched_e_category: o.cat ?? "", amount: money(o.amount), amount_paid: o.amountPaid == null ? "" : money(o.amountPaid),
    due_date: o.due ?? "", paid_date: o.paid ?? "", incurred_at: o.incurred ?? "",
    vendor: o.vendor ?? "", billable_to_owner: o.billable ?? "", escrow_covered: o.escrow ?? "",
    loan_interest: o.li == null ? "" : money(o.li), loan_principal: o.lp == null ? "" : money(o.lp),
    loan_escrow: o.le == null ? "" : money(o.le), loan_balance_after: o.lba == null ? "" : money(o.lba),
    period_key: o.period ?? "", note: o.note ?? "",
  });
}

// ── 1. RENT + LATE FEES per lease ──────────────────────────────────────────────────────────────
const acqMonth = (prop) => { const a = parseYMD(propByCode[prop].acquired); return { y: a.y, m: a.m }; };
const laterYM = (a, b) => geYM(a, b) ? a : b;

for (const L of sc.leases) {
  const p = propByCode[L.property];
  const start = parseYMD(L.start);
  // rent history start: max(window, acquisition, lease move-in for lease-ups/turnover)
  let first = laterYM(WIN_START, acqMonth(L.property));
  if (L.role === "leaseUp" || L.role === "turnoverNew") { const mi = parseYMD(L.moveIn); first = { y: mi.y, m: mi.m }; }
  // last rent month
  let last = { ...WIN };
  if (L.role === "nonRenewal") { const mo = parseYMD(L.moveOut); last = { y: mo.y, m: mo.m }; }

  const rn = parseYMD(L.renewalDate ?? "2999-01-01");
  const renewsInSpine = L.renewalRent && !L.renewalOutOfSpine;

  for (const { y, m } of months(first.y, first.m, last.y, last.m)) {
    let rent = L.rent;
    if (renewsInSpine && geYM({ y, m }, { y: rn.y, m: rn.m })) rent = L.renewalRent;
    const due = clampDay(y, m, L.dueDay);
    const period = ym(y, m);

    // prorated first month for a mid-month lease-up
    if (L.proratedFirstMonth && y === start.y && m === start.m) {
      const mi = parseYMD(L.moveIn);
      const days = dim(y, m) - mi.d + 1;
      const pro = r2(rent * days / dim(y, m));
      ev({ date: due, prop: L.property, unit: L.unit, lease: L.code, kind: "Payment", ptype: "Rent",
           status: "Paid", method: L.method, amount: pro, due, paid: clampDay(y, m, mi.d + 2), period,
           note: `prorated first month (${days}/${dim(y, m)} days) — HAND-ENTERED, worker can't prorate (gap #3)` });
      continue;
    }

    // ── L14 delinquency arc (the only non-clean payer) ──
    if (L.beat === "delinquency" && y === 2025) {
      const cure = sc.storyBeats.delinquency_L14.cureDate;
      if (m <= 3) { ev(rentRow(L, y, m, rent, due, period, clampDay(y, m, 3), "Paid")); }
      else if (m === 4 || m === 6) { // partial then cured by lump
        ev(rentRow(L, y, m, rent, due, period, cure, "Paid", null, `was Partial $400 then cured ${cure}`)); addLateFee(L, y, m, cure);
      } else if (m === 5 || m === 7) { // missed then cured
        ev(rentRow(L, y, m, rent, due, period, cure, "Paid", null, `missed then cured ${cure}`)); addLateFee(L, y, m, cure);
      } else if (m === 8) { // renewal rent, missed then cured
        ev(rentRow(L, y, m, rent, due, period, cure, "Paid", null, `renewal rent; missed then cured ${cure}`)); addLateFee(L, y, m, cure);
      } else if (m >= 9 && m <= 11) { ev(rentRow(L, y, m, rent, due, period, clampDay(y, m, 3), "Paid")); }
      else if (m === 12) { // year-end: falls behind again (fresh) — exercises delinquency aging at 12-31
        ev(rentRow(L, y, m, rent, due, period, "", "Late", null, "UNPAID at year-end (fresh miss) → delinquency current bucket"));
        addLateFee(L, y, m, "", "Late"); // assessed, unpaid
      }
      continue;
    }

    // normal payer
    const autopay = L.autopay;
    const paid = autopay ? due : clampDay(y, m, Math.min(L.dueDay + 2, dim(y, m)));
    ev(rentRow(L, y, m, rent, due, period, paid, "Paid"));
  }
}

function rentRow(L, y, m, rent, due, period, paid, status, amountPaid, note) {
  return { date: due, prop: L.property, unit: L.unit, lease: L.code, kind: "Payment", ptype: "Rent",
           status, method: L.method, amount: rent, amountPaid, due, paid: paid || "", period, note: note ?? "" };
}
function addLateFee(L, y, m, paid, status = "Paid") {
  const due = clampDay(y, m, 8); // assessed ~grace+1 for a day-1 lease
  ev({ date: due, prop: L.property, unit: L.unit, lease: L.code, kind: "Payment", ptype: "LateFee",
       status, method: L.method, amount: L.lateFee, due, paid: paid || "", period: ym(y, m),
       note: status === "Paid" ? "late fee (cured)" : "late fee assessed, unpaid at year-end" });
}

// ── 2. EXPENSES ──────────────────────────────────────────────────────────────────────────────
// Recurring templates
function recurStart(prop) { return laterYM(WIN_START, acqMonth(prop)); }
for (const rec of sc.recurring) {
  const props = rec.properties ?? [rec.property];
  for (const prop of props) {
    const s = recurStart(prop);
    for (const { y, m } of months(s.y, s.m, WIN.y, WIN.m)) {
      const monthsOk = rec.months === "all" || rec.months.includes(m);
      if (!monthsOk) continue;
      const d = clampDay(y, m, 5);
      ev({ date: d, prop, kind: "Expense", status: "Paid", cat: rec.category, amount: rec.amount,
           incurred: d, paid: d, vendor: rec.description, billable: "FALSE", escrow: "FALSE",
           note: `recurring: ${rec.code}` });
    }
  }
}
// Property tax (semi-annual) + insurance (annual). Escrow-covered when a loan covers it.
for (const p of sc.properties) {
  const hasLoan = !!p.loan;
  const escT = hasLoan && p.loan.escrowCoversTaxes;
  const escI = hasLoan && p.loan.escrowCoversInsurance;
  const s = laterYM(WIN_START, acqMonth(p.code));
  for (let y = 2023; y <= 2025; y++) {
    if (y < s.y) continue;
    const half = r2(p.annualPropertyTax / 2);
    const half2 = r2(p.annualPropertyTax - half);
    // taxes: Feb 05 and Jul 10
    if (p.annualPropertyTax > 0) {
      ev({ date: iso(y, 2, 5), prop: p.code, kind: "Expense", status: "Paid", cat: "Taxes", amount: half,
           incurred: iso(y, 2, 5), paid: iso(y, 2, 5), vendor: "Clark County Treasurer", billable: "FALSE",
           escrow: escT ? "TRUE" : "FALSE", note: escT ? "property tax H1 (escrow-disbursed)" : "property tax H1 (direct)" });
      ev({ date: iso(y, 7, 10), prop: p.code, kind: "Expense", status: "Paid", cat: "Taxes", amount: half2,
           incurred: iso(y, 7, 10), paid: iso(y, 7, 10), vendor: "Clark County Treasurer", billable: "FALSE",
           escrow: escT ? "TRUE" : "FALSE", note: escT ? "property tax H2 (escrow-disbursed)" : "property tax H2 (direct)" });
    }
    // insurance: annual, Apr 15 (escrow) or Jan 15 (direct)
    if (p.annualInsurance > 0) {
      const d = escI ? iso(y, 4, 15) : iso(y, 1, 15);
      ev({ date: d, prop: p.code, kind: "Expense", status: "Paid", cat: "Insurance", amount: p.annualInsurance,
           incurred: d, paid: d, vendor: "Buckeye Mutual Insurance", billable: "FALSE",
           escrow: escI ? "TRUE" : "FALSE", note: escI ? "hazard insurance (escrow-disbursed)" : "hazard insurance (direct)" });
    }
  }
}
// One-off repairs (2025 story) + P04 make-ready
for (const rep of sc.storyBeats.oneOffRepairs) {
  ev({ date: rep.date, prop: rep.property, unit: rep.unit, kind: "Expense", status: "Paid", cat: rep.category,
       amount: rep.amount, incurred: rep.date, paid: rep.date, vendor: rep.vendor, billable: "FALSE",
       escrow: "FALSE", note: rep.desc + (rep.artifact ? ` [${rep.artifact}]` : "") });
}
for (const mr of sc.storyBeats.turnover_P04.makeReady) {
  ev({ date: mr.date, prop: "P04", kind: "Expense", status: "Paid", cat: mr.category, amount: mr.amount,
       incurred: mr.date, paid: mr.date, vendor: mr.vendor, billable: "FALSE", escrow: "FALSE", note: "P04 make-ready: " + mr.desc });
}
// NSF fee (L04 Aug) — Other
ev({ date: "2025-08-06", prop: "P03", lease: "L04", kind: "Payment", ptype: "Other", status: "Paid", method: "Check",
     amount: 35, due: "2025-08-06", paid: "2025-08-06", period: "2025-08", note: "NSF fee (Aug check returned; re-paid)" });

// ── 3. LOAN PAYMENTS (from authoritative emitter output) ─────────────────────────────────────
const lp = readFileSync(join(CORPUS, "ledger/loan-payments.csv"), "utf8").trim().split("\n").slice(1);
const propByLoan = Object.fromEntries(sc.properties.filter(p => p.loan).map(p => [p.loan.loanNumber, p.code]));
for (const line of lp) {
  const c = line.split(",");
  const [loanNo, propCode, , periodKey, due, opening, interest, principal, escrow, total, balAfter] = c;
  const dd = parseYMD(due);
  if (dd.y < 2023) continue; // only the report window
  ev({ date: due, prop: propCode, kind: "LoanPayment", status: "Paid", amount: Number(total),
       due, paid: due, period: periodKey, li: Number(interest), lp: Number(principal), le: Number(escrow),
       lba: Number(balAfter), note: `loan ${loanNo}` });
}

// ── 4. SECURITY DEPOSITS ─────────────────────────────────────────────────────────────────────
for (const L of sc.leases) {
  const heldDate = (L.role === "leaseUp" || L.role === "turnoverNew") ? L.moveIn : "2025-01-01";
  ev({ date: heldDate, prop: L.property, unit: L.unit, lease: L.code, kind: "SDHolding", status: "Held",
       amount: L.deposit, note: "security deposit held" });
}
// L05 move-out deduction + partial return
const dd = sc.storyBeats.turnover_P04.deposit_deduction;
ev({ date: sc.storyBeats.turnover_P04.moveOut, prop: "P04", lease: "L05", kind: "SDDeduction", status: "PartiallyReturned",
     amount: dd.amount, note: dd.reason });
ev({ date: sc.storyBeats.turnover_P04.moveOut, prop: "P04", lease: "L05", kind: "SDReturn", status: "PartiallyReturned",
     amount: r2(propByCode.P04 ? 1450 - dd.amount : 0), note: `returned = 1450 held - ${dd.amount} deductions` });

// ── 5. OWNER DISTRIBUTION MEMO (computed net; NOT a P&L expense — gap #2) ─────────────────────
for (const q of sc.storyBeats.ownerDistributionMemo.quarters) {
  ev({ date: q, owner: "OE-BELL", kind: "OwnerDistributionMemo", note: "quarterly distribution to Marcus Bell = app computed net (memo only; not booked as Expense — gap #2)" });
}

// ── sort, assign ids, per-lease running owed balance ─────────────────────────────────────────
events.sort((a, b) => (a.sim_entered_date < b.sim_entered_date ? -1 : a.sim_entered_date > b.sim_entered_date ? 1 : 0));
const bal = {};
for (const e of events) {
  seq++; e.event_id = `E${String(seq).padStart(5, "0")}`;
  let rb = "";
  if (e.record_kind === "Payment" && (e.payment_type === "Rent" || e.payment_type === "LateFee") && e.lease_code) {
    const amt = Number(e.amount) || 0;
    const paid = e.status === "Paid" ? amt : e.status === "Partial" ? (Number(e.amount_paid) || 0) : 0;
    bal[e.lease_code] = r2((bal[e.lease_code] || 0) + amt - paid);
    rb = money(bal[e.lease_code]);
  }
  e.running_balance = rb;
}

// ── write ────────────────────────────────────────────────────────────────────────────────────
const cols = ["event_id","sim_entered_date","property_code","unit_code","lease_code","owner_entity","record_kind","payment_type","status","method","sched_e_category","amount","amount_paid","due_date","paid_date","incurred_at","vendor","billable_to_owner","escrow_covered","loan_interest","loan_principal","loan_escrow","loan_balance_after","period_key","running_balance","note"];
const esc = (v) => { v = String(v ?? ""); return /[",\n]/.test(v) ? `"${v.replace(/"/g, '""')}"` : v; };
const out = [cols.join(",")].concat(events.map(e => cols.map(c => esc(e[c])).join(","))).join("\n") + "\n";
writeFileSync(join(CORPUS, "ledger/events.csv"), out);

// summary
const by = {};
for (const e of events) { const k = e.record_kind + (e.payment_type ? ":" + e.payment_type : ""); by[k] = (by[k] || 0) + 1; }
console.log(`events.csv: ${events.length} rows`);
for (const [k, n] of Object.entries(by).sort()) console.log(`  ${k.padEnd(24)} ${n}`);
