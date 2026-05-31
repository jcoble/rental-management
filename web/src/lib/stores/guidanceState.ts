// Stub guidance state store (ported with shadcn UI component set from EdiPlatform)
const seen = new Set<string>();

export function isPageSeen(pageKey: string): boolean {
	return seen.has(pageKey);
}

export function markPageSeen(pageKey: string): void {
	seen.add(pageKey);
}
