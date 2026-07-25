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
  });

  it("keeps canonical receipt and tenant-account context while paging server results", () => {
    assert.match(
      pageSource,
      /accounting\.pastDue\(\{ skip, take: PAGE_SIZE \}\)/
    );
    assert.match(pageSource, /payments\.recordReceipt\(lease\.tenantAccountId/);
    assert.match(pageSource, /tenantAccount=\$\{lease\.tenantAccountId\}/);
    assert.match(pageSource, /data-testid="past-due-pagination"/);
    assert.match(pageSource, /result\.businessDate/);
    assert.doesNotMatch(pageSource, /daysFromTodayUtc|Date\.now|Tenant account #/);
    assert.doesNotMatch(pageSource, /leaseId|oldestPaymentId/);
  });
});
