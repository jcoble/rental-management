export interface OverfetchPage<T> {
	items: T[];
	hasNext: boolean;
}

export function overfetchPage<T>(rows: readonly T[], pageSize: number): OverfetchPage<T> {
	return {
		items: rows.slice(0, pageSize),
		hasNext: rows.length > pageSize,
	};
}
