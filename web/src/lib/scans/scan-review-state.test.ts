import assert from "node:assert/strict";
import { describe, it } from "node:test";

import {
  createdRecordArticle,
  createdRecordHref,
  createdRecordLabel,
  isTerminalScanReview,
  shouldDisableScanReviewControls,
} from "./scan-review-state.ts";

describe("scan review terminal state", () => {
  it("treats confirmed, rejected, and freshly confirmed scans as terminal", () => {
    assert.equal(isTerminalScanReview("Confirmed", false), true);
    assert.equal(isTerminalScanReview("Rejected", false), true);
    assert.equal(isTerminalScanReview("Reviewing", true), true);
    assert.equal(isTerminalScanReview("Reviewing", false), false);
  });

  it("disables review controls while processing or after terminal states", () => {
    assert.equal(shouldDisableScanReviewControls("Processing", false), true);
    assert.equal(shouldDisableScanReviewControls("Pending", false), true);
    assert.equal(shouldDisableScanReviewControls("Confirmed", false), true);
    assert.equal(shouldDisableScanReviewControls("Reviewing", true), true);
    assert.equal(shouldDisableScanReviewControls("Reviewing", false), false);
  });

  it("uses the right article for created record labels", () => {
    assert.equal(createdRecordArticle("Expense"), "an");
    assert.equal(createdRecordArticle("Application"), "an");
    assert.equal(createdRecordArticle("Payment"), "a");
    assert.equal(createdRecordArticle("Work Order"), "a");
  });

  it("links confirmed application scans to the application record", () => {
    assert.equal(createdRecordLabel("Application"), "Application");
    assert.equal(createdRecordHref("Application", 123), "/applications/123");
    assert.equal(createdRecordHref("Payment", 45), "/accounting/payments/45");
    assert.equal(createdRecordHref("WorkOrder", 46), "/maintenance/46");
    assert.equal(createdRecordHref("LeaseAgreement", 47), "/leases/47");
    assert.equal(createdRecordHref("Expense", 48), "/accounting/expenses/48");
    assert.equal(createdRecordHref(null, 48), "/accounting");
    assert.equal(createdRecordHref("Application", null), "/accounting");
  });

  it("links unit-tied confirmed scans to the unit command center tab", () => {
    assert.equal(
      createdRecordHref("Application", 123, 9),
      "/units/9?tab=applications&app=123"
    );
    assert.equal(
      createdRecordHref("RentalApplication", 123, 9),
      "/units/9?tab=applications&app=123"
    );
    assert.equal(
      createdRecordHref("Payment", 45, 9),
      "/units/9?tab=ledger&ledger=rent&payment=45"
    );
    assert.equal(
      createdRecordHref("WorkOrder", 46, 9),
      "/units/9?tab=maintenance&wo=46"
    );
    assert.equal(
      createdRecordHref("LeaseAgreement", 47, 9),
      "/units/9?tab=lease&lease=47"
    );
    assert.equal(
      createdRecordHref("Expense", 48, 9),
      "/units/9?tab=ledger&ledger=expenses&expense=48"
    );
  });
});
