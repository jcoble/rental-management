export const UNIT_TABS = [
  "overview",
  "listing",
  "lease",
  "applications",
  "ledger",
  "maintenance",
  "turnover",
  "documents",
  "timeline",
] as const;

export type UnitTab = (typeof UNIT_TABS)[number];

const UNIT_TAB_ALIASES: Record<string, UnitTab> = {
  rent: "ledger",
  payments: "ledger",
  expenses: "ledger",
  "make-ready": "turnover",
  makeready: "turnover",
  "move-out": "turnover",
  moveout: "turnover",
};

export function resolveUnitTab(value: string | undefined | null): UnitTab {
  const normalized = value?.trim().toLowerCase();
  if (!normalized) return "overview";
  if (UNIT_TAB_ALIASES[normalized]) return UNIT_TAB_ALIASES[normalized];
  return UNIT_TABS.includes(normalized as UnitTab)
    ? (normalized as UnitTab)
    : "overview";
}
