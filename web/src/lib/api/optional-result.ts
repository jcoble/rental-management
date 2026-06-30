export function normalizeOptionalApiResult<T>(value: T | null | undefined): T | null {
	return value ?? null;
}
