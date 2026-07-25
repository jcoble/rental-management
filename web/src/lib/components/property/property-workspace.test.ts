import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { describe, it } from "node:test";

import {
  propertyWorkspaceRoute,
  propertyWorkspaceSections,
  resolvePropertyWorkspaceEntry,
} from "./property-workspace.ts";

describe("property workspace", () => {
  const detailSource = readFileSync(
    new URL(
      "../../../routes/(protected)/properties/[id]/+page.svelte",
      import.meta.url
    ),
    "utf8"
  );

  it("opens SingleRental directly", () => {
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

    assert.equal(propertyWorkspaceRoute(entry), "/units/89");
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
    assert.equal(incompleteSingleRental.destination, "Unit");
    assert.equal(propertyWorkspaceRoute(incompleteSingleRental), null);
  });

  it("stacks property documents and history without nested tabs", () => {
    assert.match(detailSource, /DocumentsPanel/);
    assert.match(detailSource, /entityType="Property"/);
    assert.match(detailSource, /data-testid="property-documents-section"/);
    assert.match(detailSource, /data-testid="property-history-section"/);
    assert.doesNotMatch(detailSource, /property-documents-history-tabs/);
  });
});
