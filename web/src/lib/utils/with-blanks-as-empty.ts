export function withBlanksAsEmpty<T extends Record<string, unknown>>(data: T): T {
	return Object.fromEntries(
		Object.entries(data).map(([key, value]) => [key, value === null ? '' : value])
	) as T;
}
