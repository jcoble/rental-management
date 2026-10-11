#!/usr/bin/env node
// Builds the full artifact-generation manifest (data.full.json) deterministically from the frozen
// scenario + emitted loan data, for tools/generate.mjs to render. Covers every scan-ingestible doc
// type: lease PDFs (+ photo-stitch pairs), mortgage statements, rent checks, receipts (repairs /
// make-ready / tax bills / insurance), rental applications, and captioned maintenance photos.
//
// Usage: node tools/build-manifest.mjs   ->   writes tools/data.full.json
//   then: node tools/generate.mjs tools/data.full.json

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve, join } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const CORPUS = resolve(HERE, "..");
const sc = JSON.parse(readFileSync(join(CORPUS, "scenario/scenario.json"), "utf8"));
const MGMT = sc._meta.company;
const propByCode = Object.fromEntries(sc.properties.map(p => [p.code, p]));
const unitOf = (p, u) => propByCode[p].units.find(x => x.code === u);

// loan-payments 2025-01 row per loan (for the mortgage statement)
const lpLines = readFileSync(join(CORPUS, "ledger/loan-payments.csv"), "utf8").trim().split("\n").slice(1);
const jan25 = {};
for (const l of lpLines) { const c = l.split(","); if (c[3] === "2025-01") jan25[c[0]] = { opening: c[5], interest: c[6], principal: c[7], escrow: c[8], total: c[9] }; }
const loanMeta = Object.fromEntries(readFileSync(join(CORPUS, "ledger/loans.csv"), "utf8").trim().split("\n").slice(1)
  .map(l => { const c = l.split(","); return [c[0], { orig: c[4], rate: c[5], term: c[6], start: c[7], mpi: c[9], escrow: c[10] }]; }));

// helpers
const MONTHS = ["January","February","March","April","May","June","July","August","September","October","November","December"];
const comma = (n) => Number(n).toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const fmtDate = (iso) => { const [y, m, d] = iso.split("-").map(Number); return `${MONTHS[m - 1]} ${d}, ${y}`; };
const usDate = (iso) => { const [y, m, d] = iso.split("-"); return `${m}/${d}/${y}`; };
const ord = (n) => n + (["th","st","nd","rd"][(n % 100 >> 3 ^ 1 && n % 10) < 4 ? n % 10 : 0] || "th");
const ordinal = (n) => { const s = ["th","st","nd","rd"], v = n % 100; return n + (s[(v - 20) % 10] || s[v] || s[0]); };
const emailOf = (name) => name.toLowerCase().replace(/[^a-z ]/g, "").replace(/\s+/g, ".") + "@example.com";
const phoneOf = (seed) => { let h = 0; for (const c of seed) h = (h * 31 + c.charCodeAt(0)) % 9000; return `(937) 555-${String(1000 + h).slice(0, 4)}`; };
const addMonths = (iso, n) => { let [y, m, d] = iso.split("-").map(Number); m += n; y += Math.floor((m - 1) / 12); m = ((m - 1) % 12) + 1; return `${y}-${String(m).padStart(2, "0")}-${String(d).padStart(2, "0")}`; };
function words(n) { // integer 0..999999 to words
  const ones = ["zero","one","two","three","four","five","six","seven","eight","nine","ten","eleven","twelve","thirteen","fourteen","fifteen","sixteen","seventeen","eighteen","nineteen"];
  const tens = ["","","twenty","thirty","forty","fifty","sixty","seventy","eighty","ninety"];
  const u3 = (x) => { let s = ""; if (x >= 100) { s += ones[Math.floor(x / 100)] + " hundred "; x %= 100; } if (x >= 20) { s += tens[Math.floor(x / 10)] + (x % 10 ? "-" + ones[x % 10] : "") + " "; } else if (x > 0) s += ones[x] + " "; return s.trim(); };
  if (n === 0) return "zero";
  let s = ""; if (n >= 1000) { s += u3(Math.floor(n / 1000)) + " thousand "; n %= 1000; } s += u3(n); return s.trim();
}
const amountWords = (amt) => { const d = Math.floor(amt), c = Math.round((amt - d) * 100); const w = words(d); return w.charAt(0).toUpperCase() + w.slice(1) + ` and ${String(c).padStart(2, "0")}/100`; };

const items = [];
const provisions = "No smoking inside the premises. Renter's insurance required. Tenant maintains the lawn where noted; Landlord maintains major systems and appliances. Quiet hours 10pm-7am.";

// ── 1. LEASE PDFs (all 22) + a few photo-stitch pairs ────────────────────────────────────────
const photoStitch = new Set(["L01", "L11", "L14"]);
for (const L of sc.leases) {
  const p = propByCode[L.property], u = unitOf(L.property, L.unit);
  const ln = `L-${L.start.slice(0, 4)}-${L.property}${L.unit}`;
  const data = {
    managementCompany: MGMT, leaseNumber: ln, signDate: fmtDate(addMonths(L.start, 0).replace(/-\d\d$/, "-01")),
    tenantName: L.tenant, tenantEmail: emailOf(L.tenant), tenantPhone: phoneOf(L.tenant),
    propertyAddress: p.address.line1, unitNumber: u.unitNumber, propertyCity: p.address.city,
    propertyState: p.address.state, propertyPostal: p.address.postal, bedrooms: u.beds, bathrooms: u.baths,
    squareFeet: comma(u.sqft).replace(".00", ""), startDate: fmtDate(L.start), endDate: fmtDate(L.end),
    monthlyRent: comma(L.rent), rentDueDayOrdinal: ordinal(L.dueDay), securityDeposit: comma(L.deposit),
    lateFee: comma(L.lateFee), paymentMethod: L.method === "Autopay" ? "ACH autopay" : L.method, additionalProvisions: provisions,
  };
  items.push({ template: "templates/lease.html", out: `generated/leases/${L.code}_${L.property}${L.unit}.pdf`, mode: "pdf", data });

  if (photoStitch.has(L.code)) {
    const hdr = `<h1>RESIDENTIAL LEASE AGREEMENT</h1><div class="sub">State of Ohio &middot; Lease No. ${ln} &middot; ${MGMT}</div>`;
    const p1 = hdr + `<h2>Parties &amp; Premises</h2><table><tr><td class="k">Tenant</td><td class="v">${L.tenant}</td></tr>${(L.coTenants || []).map(c => `<tr><td class="k">Co-tenant</td><td class="v">${c}</td></tr>`).join("")}<tr><td class="k">Email</td><td class="v">${emailOf(L.tenant)}</td></tr><tr><td class="k">Premises</td><td class="v">${p.address.line1}, Unit ${u.unitNumber}<br>${p.address.city}, ${p.address.state} ${p.address.postal}</td></tr></table><h2>Term &amp; Rent</h2><table><tr><td class="k">Commencement</td><td class="v">${fmtDate(L.start)}</td></tr><tr><td class="k">Expiration</td><td class="v">${fmtDate(L.end)}</td></tr><tr><td class="k">Monthly rent</td><td class="v">$${comma(L.rent)}</td></tr><tr><td class="k">Rent due day</td><td class="v">${ordinal(L.dueDay)} of the month</td></tr><tr><td class="k">Security deposit</td><td class="v">$${comma(L.deposit)}</td></tr><tr><td class="k">Late fee</td><td class="v">$${comma(L.lateFee)}</td></tr></table><p>Tenant agrees to pay Monthly Rent in advance. Page 1 of 2; signatures follow.</p>`;
    const p2 = `<h2>Signatures</h2><p>By signing, the parties agree to all terms for ${p.address.line1}, Unit ${u.unitNumber}, ${p.address.city}, ${p.address.state}.</p><div class="sigline">Tenant &mdash; ${L.tenant}</div><div class="sigline">Landlord / Agent &mdash; ${MGMT}</div><p style="margin-top:18px">Executed ${fmtDate(L.start)} at ${p.address.city}, Ohio.</p>`;
    items.push({ template: "templates/lease-photo-page.html", out: `generated/leases/photos/${L.code}_p1.jpg`, mode: "photo", size: [1200, 1600], data: { leaseNumber: ln, pageLabel: "page 1 of 2", rotate: "-1.4", bodyHtml: p1 } });
    items.push({ template: "templates/lease-photo-page.html", out: `generated/leases/photos/${L.code}_p2.jpg`, mode: "photo", size: [1200, 1600], data: { leaseNumber: ln, pageLabel: "page 2 of 2", rotate: "1.2", bodyHtml: p2 } });
  }
}

// ── 2. MORTGAGE STATEMENTS (11) ──────────────────────────────────────────────────────────────
for (const p of sc.properties.filter(p => p.loan)) {
  const ln = p.loan.loanNumber, m = loanMeta[ln], j = jan25[ln];
  items.push({ template: "templates/mortgage-statement.html", out: `generated/mortgage/${p.code}_${ln}.jpg`, mode: "photo", size: [1200, 1600], data: {
    lender: p.loan.lender, loanNumber: ln, propertyAddress: `${p.address.line1}, ${p.address.city} ${p.address.state}`,
    statementDate: "01/05/2025", dueDate: "02/01/2025", totalPayment: comma(j.total), currentBalance: comma(j.opening),
    originalAmount: comma(m.orig), interestRate: m.rate, termMonths: m.term, startDate: usDate(m.start),
    maturityDate: usDate(addMonths(m.start, Number(m.term))), principalInterest: comma(m.mpi), escrow: comma(m.escrow),
    principalPortion: comma(j.principal), interestPortion: comma(j.interest),
    escrowCovers: p.loan.escrowCoversInsurance ? "County property tax & hazard insurance" : "County property tax",
  } });
}

// ── 3. RENT CHECKS (scannable payers × sample months) ────────────────────────────────────────
const checkPayers = sc.leases.filter(L => ["Check", "MoneyOrder"].includes(L.method));
const checkMonths = [1, 3, 4, 7, 10, 12];
let checkNo = 1204;
for (const L of checkPayers) {
  const p = propByCode[L.property], u = unitOf(L.property, L.unit);
  for (const mo of checkMonths) {
    const rent = (L.renewalRent && !L.renewalOutOfSpine && mo >= Number((L.renewalDate || "2999-01").slice(5, 7)) && "2025" >= (L.renewalDate || "2999").slice(0, 4)) ? L.renewalRent : L.rent;
    checkNo++;
    items.push({ template: "templates/rent-check.html", out: `generated/checks/${L.code}_2025-${String(mo).padStart(2, "0")}.jpg`, mode: "photo", size: [1200, 820], data: {
      payerName: L.tenant, payerAddress: `${p.address.line1} Unit ${u.unitNumber}, ${p.address.city} OH`,
      bankName: L.method === "MoneyOrder" ? "USPS Money Order" : "Springfield Community Bank",
      checkNumber: L.method === "MoneyOrder" ? `MO${checkNo}` : String(checkNo), date: usDate(`2025-${String(mo).padStart(2, "0")}-03`),
      payee: MGMT, amount: comma(rent), amountWords: amountWords(rent), memo: `Rent ${MONTHS[mo - 1]} 2025 - ${p.address.line1} #${u.unitNumber}`,
      routing: "044072324", account: "•••••" + (4100 + (checkNo % 900)),
    } });
  }
}

// ── 4. RECEIPTS: one-off repairs + make-ready + tax bills + insurance ─────────────────────────
let rcpt = 58200;
const receipt = (date, prop, vendor, cat, total, desc, kind = "Receipt", unit = "") => {
  const p = propByCode[prop]; const isTax = kind === "PropertyTax"; const isIns = kind === "Insurance";
  const sub = isTax || isIns ? total : Math.round(total / 1.0725 * 100) / 100; const tax = isTax || isIns ? 0 : Math.round((total - sub) * 100) / 100;
  items.push({ template: "templates/receipt.html", out: `generated/receipts/${date}_${prop}_${(++rcpt)}.jpg`, mode: "photo", size: [1200, 1600], data: {
    vendorName: vendor, vendorAddress: `Springfield, OH 455${(rcpt % 90) + 10}`, vendorPhone: phoneOf(vendor), vendorWebsite: vendor.toLowerCase().split(" ")[0] + ".example.com",
    receiptNumber: `${isTax ? "TAX" : isIns ? "INS" : "R"}-${rcpt}`, transactionDate: usDate(date),
    serviceAddress: `${p.address.line1}${unit ? " #" + unit : ""}`, lineItemsHtml: `<div class="row"><span class="d">${desc}</span><span>$${comma(sub)}</span></div>`,
    subtotal: comma(sub), taxRate: isTax || isIns ? "0.00" : "7.25", tax: comma(tax), total: comma(total),
    paymentMethod: isTax ? "Escrow/ACH" : "Visa", cardLast4: String(6000 + (rcpt % 900)), notes: `${desc} — category ${cat}. ${kind === "Receipt" ? "PAID" : kind}.`,
  } });
};
for (const r of sc.storyBeats.oneOffRepairs) receipt(r.date, r.property, r.vendor, r.category, r.amount, r.desc, "Receipt", r.unit);
for (const mr of sc.storyBeats.turnover_P04.makeReady) receipt(mr.date, "P04", mr.vendor, mr.category, mr.amount, mr.desc);
for (const code of ["P03", "P08"]) { const p = propByCode[code]; // direct-pay property tax + insurance bills
  receipt("2025-02-05", code, "Clark County Treasurer", "Taxes", Math.round(p.annualPropertyTax / 2 * 100) / 100, "Property tax 2025 first half", "PropertyTax");
  receipt("2025-07-10", code, "Clark County Treasurer", "Taxes", Math.round(p.annualPropertyTax / 2 * 100) / 100, "Property tax 2025 second half", "PropertyTax");
  receipt("2025-01-15", code, "Buckeye Mutual Insurance", "Insurance", p.annualInsurance, "Annual landlord hazard policy 2025", "Insurance");
}

// ── 5. RENTAL APPLICATIONS (lease-ups + declined) ────────────────────────────────────────────
const apps = [
  { name: "Ravi Anand", prop: "P05", unit: "3", income: 3400, moveIn: "2025-03-01", employer: "Springfield Regional Hospital", date: "2025-02-01" },
  { name: "Sofia Marin", prop: "P09", unit: "4", income: 3100, moveIn: "2025-03-15", employer: "Clark State College", date: "2025-02-01" },
  { name: "Derek Cole", prop: "P04", unit: "U1", income: 5200, moveIn: "2025-07-01", employer: "Honda of America Mfg.", date: "2025-06-15" },
  { name: "Marcus Webb", prop: "P05", unit: "3", income: 2100, moveIn: "2025-03-01", employer: "(gig / rideshare)", date: "2025-02-01", note: "declined" },
];
for (const a of apps) { const p = propByCode[a.prop], u = unitOf(a.prop, a.unit); const [fn, ...ln] = a.name.split(" ");
  items.push({ template: "templates/application.html", out: `generated/applications/${a.name.replace(/\s+/g, "-")}_${a.prop}${a.unit}.pdf`, mode: "pdf", data: {
    managementCompany: MGMT, firstName: fn, lastName: ln.join(" "), email: emailOf(a.name), phone: phoneOf(a.name),
    dob: "0" + ((a.name.length % 9) + 1) + "/14/1991", idLast4: String(3000 + (a.income % 900)), currentAddress: `${100 + (a.income % 800)} Prior St, Springfield OH`,
    employer: a.employer, monthlyIncome: comma(a.income), coSigner: "—", propertyName: p.name, unitNumber: u.unitNumber,
    applyingFor: `${u.beds}bd/${u.baths}ba at ${p.address.line1}`, desiredMoveIn: fmtDate(a.moveIn), signDate: fmtDate(a.date),
  } });
}

// ── 6. MAINTENANCE PHOTOS (captioned placeholders — Q4 photo path) ───────────────────────────
const maint = [
  { title: "Leaky kitchen faucet", prop: "P09", unit: "2", date: "2025-01-12", desc: "Steady drip under sink; cabinet base damp.", c: ["#2b3a4a","#1c2733","#141b24"], sc: "#3d5266" },
  { title: "AC not cooling", prop: "P09", unit: "3", date: "2025-06-12", desc: "Unit runs but blows warm; ~82F indoors.", c: ["#3a2f2a","#241d19","#161110"], sc: "#5a463a" },
  { title: "Garbage disposal jammed", prop: "P09", unit: "1", date: "2025-08-20", desc: "Hums, won't spin; standing water in sink.", c: ["#2a3330","#1a201e","#111514"], sc: "#3c4a45" },
  { title: "Roof leak — ceiling stain", prop: "P02", unit: "A", date: "2025-08-15", desc: "Brown stain spreading in back bedroom ceiling after rain.", c: ["#33302a","#201e19","#141210"], sc: "#4a4438" },
  { title: "Water heater failed", prop: "P13", unit: "A", date: "2025-11-10", desc: "No hot water; tank leaking at base in utility closet.", c: ["#26313b","#181f26","#0f1318"], sc: "#374857" },
];
for (const m of maint) { const p = propByCode[m.prop];
  items.push({ template: "templates/maintenance-photo.html", out: `generated/maintenance/${m.prop}${m.unit}_${m.date}.jpg`, mode: "photo", size: [1200, 1600], data: {
    title: m.title, location: `${p.address.line1} #${m.unit}`, description: m.desc, date: usDate(m.date), icon: "⚠",
    rot: "-2", c1: m.c[0], c2: m.c[1], c3: m.c[2], sc1: m.sc,
  } });
}

writeFileSync(join(HERE, "data.full.json"), JSON.stringify(items, null, 1) + "\n");
const byType = {};
for (const it of items) { const t = it.out.split("/")[1]; byType[t] = (byType[t] || 0) + 1; }
console.log(`data.full.json: ${items.length} artifacts`);
for (const [t, n] of Object.entries(byType).sort()) console.log(`  ${t.padEnd(14)} ${n}`);
