/**
 * Best-effort parser for a legacy single-line US mailing address into structured parts.
 *
 * Owners created before structured addresses existed carry only a single-line `address`
 * (e.g. "88 Westview Ct, Gahanna, OH 43230"). When such an owner is edited, seeding the
 * structured `addressLine1` field with the WHOLE legacy line and then letting the user fill
 * in city/state/zip duplicates the suffix — the composed display and saved data both end up
 * with ", Gahanna, OH 43230" twice. Splitting the legacy line up front avoids that.
 *
 * Expected shape (the common case): `line1[, line2], city, ST ZIP`. We peel the trailing
 * "ST ZIP" token, then the segment before it as the city, and rejoin the remainder as line1.
 * Anything that doesn't match cleanly falls back to line1-only so we never lose data or guess
 * wrong — the user can still correct it by hand.
 */
export interface ParsedAddress {
	addressLine1: string;
	city: string;
	state: string;
	postalCode: string;
}

// Trailing "ST 12345" or "ST 12345-6789" (2-letter state + 5/9-digit ZIP).
const STATE_ZIP = /^([A-Za-z]{2})\s+(\d{5}(?:-\d{4})?)$/;

/**
 * Parse a legacy single-line address. Returns structured parts; on anything it can't
 * confidently split, returns the whole input as `addressLine1` and empty city/state/zip.
 */
export function parseLegacyAddress(line: string | null | undefined): ParsedAddress {
	const raw = (line ?? '').trim();
	const fallback: ParsedAddress = { addressLine1: raw, city: '', state: '', postalCode: '' };
	if (!raw) return fallback;

	const parts = raw.split(',').map((p) => p.trim()).filter((p) => p.length > 0);
	// Need at least "line1, city, ST ZIP" → 3 comma-separated segments to split confidently.
	if (parts.length < 3) return fallback;

	const last = parts[parts.length - 1];
	const m = STATE_ZIP.exec(last);
	if (!m) return fallback;

	const state = m[1].toUpperCase();
	const postalCode = m[2];
	const city = parts[parts.length - 2];
	const addressLine1 = parts.slice(0, parts.length - 2).join(', ');
	if (!addressLine1 || !city) return fallback;

	return { addressLine1, city, state, postalCode };
}

/**
 * Belt-and-suspenders for save: if structured city/state/zip are present and `line1` still
 * ends with the composed ", City, ST ZIP" suffix (because it was seeded from the legacy line),
 * strip that suffix so the stored line1 is just the street. No-op when nothing matches.
 */
export function stripComposedSuffix(
	addressLine1: string,
	city: string,
	state: string,
	postalCode: string
): string {
	const line1 = (addressLine1 ?? '').trim();
	const c = (city ?? '').trim();
	const s = (state ?? '').trim();
	const z = (postalCode ?? '').trim();
	if (!line1 || !c || !s || !z) return line1;

	// Match a trailing ", City, ST ZIP" (case-insensitive, tolerant of extra inner whitespace).
	const esc = (v: string) => v.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
	const suffix = new RegExp(`,\\s*${esc(c)}\\s*,\\s*${esc(s)}\\s+${esc(z)}\\s*$`, 'i');
	const stripped = line1.replace(suffix, '').trim();
	// Don't strip down to nothing — if line1 WAS only the composed tail, keep the original.
	return stripped.length > 0 ? stripped : line1;
}
