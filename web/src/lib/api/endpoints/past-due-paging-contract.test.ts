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
const previewSource = readFileSync(
  new URL("../../accounting/past-due-preview.ts", import.meta.url),
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

  it("uses the bounded production preview loader with fail-closed continuation checks", () => {
    assert.match(pageSource, /import \{ loadAllOpenCharges \} from '\$lib\/accounting\/past-due-preview';/);
    assert.match(pageSource, /queryFn: \(\) => loadAllOpenCharges\(tenantAccountId as number\)/);
    assert.match(previewSource, /MAX_OPEN_CHARGE_PREVIEW_REQUESTS = 32/);
    assert.match(previewSource, /for \(let requestNumber = 0; requestNumber < MAX_OPEN_CHARGE_PREVIEW_REQUESTS/);
    assert.match(previewSource, /page\.skip !== nextSkip/);
    assert.match(previewSource, /page\.totalCount !== expectedTotalCount/);
    assert.match(previewSource, /Open-charge preview exceeded/);
    assert.match(previewSource, /openOnly: true/);
    assert.match(previewSource, /sort: 'oldestDueOn'/);
    assert.match(pageSource, /openChargesQuery\.data \?\? \[\]/);
  });
});
