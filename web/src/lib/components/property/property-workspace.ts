import type { Property, RentalStructure } from "$lib/types";

export const propertyWorkspaceSections = [
  { id: "summary", label: "Summary" },
  { id: "rentals", label: "Rentals" },
  { id: "ownership-management", label: "Ownership & management" },
  { id: "property-work", label: "Property work" },
  { id: "property-finances", label: "Property finances" },
  { id: "documents-history", label: "Documents & history" },
] as const;

export type PropertyWorkspaceSection =
  (typeof propertyWorkspaceSections)[number]["id"];

export interface PropertyWorkspaceEntry {
  destination: "Property" | "Unit";
  propertyId: number;
  unitId?: number | null;
  areas: string[];
}

export type PropertyWithWorkspaceEntry = Property & {
  workspaceEntry: PropertyWorkspaceEntry;
};

/**
 * Resolves a collection row using persisted RentalStructure and the server-selected canonical
 * destination. Unit count and Property type are intentionally absent: neither may infer structure.
 */
export function resolvePropertyWorkspaceEntry(source: {
  id: number;
  rentalStructure: RentalStructure;
  workspaceEntry?: PropertyWorkspaceEntry | null;
}): PropertyWorkspaceEntry {
  const serverEntry = source.workspaceEntry;
  if (source.rentalStructure === "SingleRental") {
    return {
      destination: "Unit",
      propertyId: source.id,
      unitId:
        serverEntry?.destination === "Unit" &&
        serverEntry.propertyId === source.id
          ? serverEntry.unitId
          : null,
      areas: [],
    };
  }

  return {
    destination: "Property",
    propertyId: source.id,
    unitId: null,
    areas: serverEntry?.destination === "Property" ? serverEntry.areas : [],
  };
}

export function propertyWorkspaceRoute(
  entry: PropertyWorkspaceEntry,
  section: PropertyWorkspaceSection = "summary"
): string | null {
  if (entry.destination === "Unit") {
    return entry.unitId != null && entry.unitId > 0
      ? `/units/${entry.unitId}`
      : null;
  }
  return `/properties/${entry.propertyId}?area=${section}`;
}

export function readPropertyWorkspaceSection(
  value: string | null
): PropertyWorkspaceSection {
  return propertyWorkspaceSections.some((section) => section.id === value)
    ? (value as PropertyWorkspaceSection)
    : "summary";
}
