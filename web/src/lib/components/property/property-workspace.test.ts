import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { describe, it } from "node:test";

import {
  propertyWorkspaceRoute,
  propertyWorkspaceSections,
  resolvePropertyWorkspaceEntry,
  unitCommandCenterRoute,
} from "./property-workspace.ts";

describe("property workspace", () => {
  const detailSource = readFileSync(
    new URL(
      "../../../routes/(protected)/properties/[id]/+page.svelte",
      import.meta.url
    ),
    "utf8"
  );

  it("opens SingleRental property details while preserving its Unit command route", () => {
    const entry = resolvePropertyWorkspaceEntry({
      id: 41,
      rentalStructure: "SingleRental",
      workspaceEntry: {
        destination: "Unit",
        propertyId: 41,
        unitId: 89,
        areas: [],
      },
    });

    assert.equal(entry.destination, "Property");
    assert.equal(propertyWorkspaceRoute(entry), "/properties/41?area=summary");
    assert.equal(unitCommandCenterRoute(entry), "/units/89");
  });

  it("shows six MultiRental areas", () => {
    assert.deepEqual(
      propertyWorkspaceSections.map((section) => section.label),
      [
        "Summary",
        "Rentals",
        "Ownership & management",
        "Property work",
        "Property finances",
        "Documents & history",
      ]
    );
    const entry = resolvePropertyWorkspaceEntry({
      id: 42,
      rentalStructure: "MultiRental",
      workspaceEntry: {
        destination: "Property",
        propertyId: 42,
        unitId: null,
        areas: [
          "Summary",
          "Rentals",
          "OwnershipManagement",
          "PropertyWork",
          "PropertyFinances",
          "DocumentsHistory",
        ],
      },
    });
    assert.equal(
      propertyWorkspaceRoute(entry, "property-work"),
      "/properties/42?area=property-work"
    );
  });

  it("never infers structure", () => {
    const partiallyEnteredBuilding = resolvePropertyWorkspaceEntry({
      id: 43,
      rentalStructure: "MultiRental",
      workspaceEntry: {
        destination: "Property",
        propertyId: 43,
        unitId: null,
        areas: [],
      },
    });
    assert.equal(partiallyEnteredBuilding.destination, "Property");

    const incompleteSingleRental = resolvePropertyWorkspaceEntry({
      id: 44,
      rentalStructure: "SingleRental",
      workspaceEntry: null,
    });
    assert.equal(incompleteSingleRental.destination, "Property");
    assert.equal(propertyWorkspaceRoute(incompleteSingleRental), "/properties/44?area=summary");
    assert.equal(unitCommandCenterRoute(incompleteSingleRental), null);
  });

  it("stacks property documents and history without nested tabs", () => {
    assert.match(detailSource, /DocumentsPanel/);
    assert.match(detailSource, /entityType="Property"/);
    assert.match(detailSource, /data-testid="property-documents-section"/);
    assert.match(detailSource, /data-testid="property-history-section"/);
    assert.doesNotMatch(detailSource, /property-documents-history-tabs/);
  });

  it("keeps the property detail editor available for SingleRental fields", () => {
    assert.doesNotMatch(detailSource, /goto\(`\/units\/\$\{loaded\.workspaceEntry\.unitId\}`/);
    assert.match(detailSource, /data-testid="property-detail-edit"/);
    assert.match(detailSource, /propertyOperationsSchema/);
    assert.match(detailSource, /propertyBasisSchema/);
    assert.match(detailSource, /property-detail-year-built/);
    assert.match(detailSource, /property-detail-management-fee/);
    assert.match(detailSource, /property-detail-notes/);
    assert.match(detailSource, /property-detail-purchase-price/);
    assert.match(detailSource, /property-detail-land-value/);
    assert.match(detailSource, /property-detail-in-service-date/);
    assert.match(detailSource, /property-detail-manual-depreciation/);
    assert.match(detailSource, /property-detail-accumulated-depreciation/);
  });
});
