import { readFileSync } from "node:fs";
import assert from "node:assert/strict";
import { describe, it } from "node:test";

const source = readFileSync(
  new URL(
    "../../routes/(protected)/scan/[draftId]/+page.svelte",
    import.meta.url
  ),
  "utf8"
);

describe("scan review reliability contract", () => {
  it("keeps a realtime-independent refresh path while extraction is processing", () => {
    assert.match(source, /refetchIntervalInBackground:\s*true/);
    assert.match(source, /refetchOnReconnect:\s*true/);
    assert.match(
      source,
      /setInterval\(\(\)\s*=>\s*\{\s*if \(!draftQuery\.isFetching\) void draftQuery\.refetch\(\);/s
    );
  });

  it("lets expense review select a work order without client-side filtering", () => {
    assert.match(source, /data-testid="scan-expense-work-order-scope"/);
    assert.match(source, /data-testid="scan-expense-work-order-select"/);
    assert.match(
      source,
      /workOrders\.listPage\(getCurrentPortfolioId\(\),\s*\{/
    );
    assert.match(
      source,
      /propertyId:\s*selectedExpensePropertyId \?\? undefined/
    );
    assert.match(
      source,
      /unitId:\s*selectedExpenseUnitIdNumber \?\? undefined/
    );
    assert.doesNotMatch(
      source,
      /expenseWorkOrderChoices\s*=\s*\$derived\([^)]*\.filter\(/s
    );
  });

  it("submits the selected expense work order and keeps property/unit consistent", () => {
    assert.match(
      source,
      /overrides\['workOrderId'\]\s*=\s*Number\(selectedExpenseWorkOrderId\)/
    );
    assert.match(
      source,
      /selectedPropertyId\s*=\s*String\(selected\.propertyId\)/
    );
    assert.match(
      source,
      /selectedExpenseUnitId\s*=\s*selected\.unitId \? String\(selected\.unitId\) : NO_UNIT/
    );
    assert.match(source, /function selectContextPropertyId/);
    assert.match(source, /clearExpenseWorkOrderSelection\(\)/);
  });
});
