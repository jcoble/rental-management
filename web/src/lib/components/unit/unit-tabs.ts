export const UNIT_TABS = ['overview', 'lease', 'applications', 'rent', 'maintenance', 'documents', 'expenses', 'timeline'] as const;

export type UnitTab = (typeof UNIT_TABS)[number];

export function resolveUnitTab(value: string | undefined | null): UnitTab {
	return UNIT_TABS.includes(value as UnitTab) ? (value as UnitTab) : 'overview';
}
