/**
 * IANA time-zone reference data for the shared `TimeZoneSelect.svelte` picker.
 *
 * The canonical list comes from the runtime — `Intl.supportedValuesOf('timeZone')`
 * — so it always matches what the platform (and the .NET backend) can resolve, and
 * never goes stale. We split it into two groups:
 *
 *   COMMON_US_TIME_ZONES  — the handful a US landlord almost always wants, surfaced
 *                           at the top of the dropdown with friendly labels.
 *   `timeZoneItems()`     — common US zones first, then every remaining IANA zone
 *                           alphabetically, de-duplicated.
 *
 * The bound value is always the canonical IANA id (e.g. "America/New_York"); the
 * backend `Portfolio.TimeZone` stays a plain string, so this is a drop-in swap for
 * the old free-form text input.
 */

export type TimeZoneOption = { value: string; label: string };

/**
 * Curated common US zones, ordered roughly east -> west, with friendly labels.
 * Only ids that the runtime actually supports are surfaced (filtered below).
 */
export const COMMON_US_TIME_ZONES: readonly TimeZoneOption[] = [
	{ value: 'America/New_York', label: 'Eastern Time — New York (America/New_York)' },
	{ value: 'America/Chicago', label: 'Central Time — Chicago (America/Chicago)' },
	{ value: 'America/Denver', label: 'Mountain Time — Denver (America/Denver)' },
	{ value: 'America/Phoenix', label: 'Mountain Time, no DST — Phoenix (America/Phoenix)' },
	{ value: 'America/Los_Angeles', label: 'Pacific Time — Los Angeles (America/Los_Angeles)' },
	{ value: 'America/Anchorage', label: 'Alaska Time — Anchorage (America/Anchorage)' },
	{ value: 'Pacific/Honolulu', label: 'Hawaii Time — Honolulu (Pacific/Honolulu)' }
];

/** All IANA zones the runtime supports, alphabetical. Falls back to UTC if the API is missing. */
function supportedTimeZones(): readonly string[] {
	try {
		const fn = (Intl as unknown as { supportedValuesOf?: (k: string) => string[] })
			.supportedValuesOf;
		const list = fn ? fn('timeZone') : [];
		return list.length ? list : ['UTC'];
	} catch {
		return ['UTC'];
	}
}

/**
 * Picker items: common US zones first (only those actually supported), then every
 * remaining IANA zone alphabetically. The label includes the canonical id so a
 * search for either the city ("Denver") or the id ("America/Denver") matches.
 */
export function timeZoneItems(): TimeZoneOption[] {
	const supported = supportedTimeZones();
	const supportedSet = new Set(supported);

	const common = COMMON_US_TIME_ZONES.filter((z) => supportedSet.has(z.value));
	const commonIds = new Set(common.map((z) => z.value));

	const rest = supported
		.filter((id) => !commonIds.has(id))
		.map((id) => ({ value: id, label: id }));

	return [...common, ...rest];
}

/** True if `id` is a runtime-supported IANA zone. */
export function isValidTimeZone(id: string | null | undefined): boolean {
	if (!id) return false;
	return new Set(supportedTimeZones()).has(id);
}

/**
 * Best-effort guess of the browser's current IANA zone, falling back to
 * America/New_York (the prior free-form default) when unavailable/unsupported.
 */
export function guessBrowserTimeZone(): string {
	try {
		const tz = Intl.DateTimeFormat().resolvedOptions().timeZone;
		if (tz && isValidTimeZone(tz)) return tz;
	} catch {
		/* ignore */
	}
	return 'America/New_York';
}
