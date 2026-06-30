import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { recordHref } from "./record-href.ts";

describe("recordHref", () => {
  it("routes a unit-tied work order to the unit Maintenance tab", () => {
    assert.equal(
      recordHref("workOrder", { id: 12, unitId: 3 }),
      "/units/3?tab=maintenance&wo=12"
    );
  });
  it("routes a property-only work order (no unit) to the generic page", () => {
    assert.equal(
      recordHref("workOrder", { id: 12, unitId: null }),
      "/maintenance/12"
    );
  });
  it("routes lease / expense / payment / application to the right tab + param", () => {
    assert.equal(
      recordHref("lease", { id: 5, unitId: 3 }),
      "/units/3?tab=lease&lease=5"
    );
    assert.equal(
      recordHref("expense", { id: 7, unitId: 3 }),
      "/units/3?tab=ledger&ledger=expenses&expense=7"
    );
    assert.equal(
      recordHref("payment", { id: 9, unitId: 3 }),
      "/units/3?tab=ledger&ledger=rent&payment=9"
    );
    assert.equal(
      recordHref("application", { id: 2, unitId: 3 }),
      "/units/3?tab=applications&app=2"
    );
  });
  it("falls back to the generic page when unitId is missing/0", () => {
    assert.equal(
      recordHref("expense", { id: 7, unitId: 0 }),
      "/accounting/expenses/7"
    );
    assert.equal(recordHref("payment", { id: 9 }), "/accounting/payments/9");
    assert.equal(
      recordHref("application", { id: 2, unitId: null }),
      "/applications/2"
    );
  });
});
