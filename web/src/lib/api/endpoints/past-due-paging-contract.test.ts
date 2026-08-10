import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { describe, it } from "node:test";

const endpointSource = readFileSync(
  new URL("./accounting.ts", import.meta.url),
  "utf8"
);
const typeSource = readFileSync(
  new URL("../../types/index.ts", import.meta.url),
  "utf8"
);
const pageSource = readFileSync(
  new URL(
    "../../../routes/(protected)/accounting/past-due/+page.svelte",
    import.meta.url
  ),
  "utf8"
);

describe("Who's behind canonical paging contract", () => {
  it("sends bounded skip/take and exposes exact page metadata", () => {
    assert.match(
      endpointSource,
      /pastDue: \(params: Pick<ListParams, 'skip' \| 'take'>/
    );
    assert.match(endpointSource, /buildListQuery\(params\)/);
    assert.match(
      typeSource,
      /interface PastDueResponse[\s\S]*businessDate: string \| null;[\s\S]*skip: number;[\s\S]*take: number;/
    );
    assert.match(
      typeSource,
      /interface PastDueLease[\s\S]*pastDueAmount: number;[\s\S]*totalOpenBalance: number;[\s\S]*oldestLedgerEntryId: number;[\s\S]*oldestLedgerEntryOpenAmount: number;/
    );
  });

  it("keeps canonical receipt and tenant-account context while paging server results", () => {
    assert.match(
      pageSource,
      /accounting\.pastDue\(\{ skip, take: PAGE_SIZE \}\)/
    );
    assert.match(pageSource, /payments\.recordReceipt\(lease\.tenantAccountId/);
    assert.match(pageSource, /targetChargeEntryId: null/);
    assert.match(pageSource, /amount: data\.amount/);
    assert.match(pageSource, /allocateOldestCharges: true/);
    assert.match(pageSource, /tenantAccount=\$\{lease\.tenantAccountId\}/);
    assert.match(pageSource, /data-testid="past-due-pagination"/);
    assert.match(pageSource, /result\.businessDate/);
    assert.doesNotMatch(pageSource, /daysFromTodayUtc|Date\.now|Tenant account #/);
    assert.doesNotMatch(pageSource, /leaseId|oldestPaymentId/);
  });

  it("walks every server-ordered open-charge page for the payment preview", () => {
    assert.match(pageSource, /async function loadAllOpenCharges\(tenantAccountId: number\)/);
    assert.match(pageSource, /skip: nextSkip/);
    assert.match(pageSource, /take: OPEN_CHARGES_PAGE_SIZE/);
    assert.match(pageSource, /openOnly: true/);
    assert.match(pageSource, /sort: 'oldestDueOn'/);
    assert.match(pageSource, /while \(nextSkip < totalCount\)/);
    assert.match(pageSource, /openChargesQuery\.data \?\? \[\]/);
  });
});
