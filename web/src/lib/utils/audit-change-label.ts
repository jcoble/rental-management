/**
 * Landlord-facing wording for the activity history.
 *
 * The API already splits each changed property into spaced words (see AuditDiffBuilder.Humanize),
 * so entries arrive as "Updated at utc", "Issued artifact id", "Possession agreement exception
 * reason". Those are still database words. These helpers drop the bookkeeping columns entirely and
 * say the rest the way a landlord would. PascalCase input is handled too, so the helpers keep
 * working if a caller passes a raw property name.
 */

/** Split PascalCase/underscored names into lowercase words; already-spaced names pass through. */
function toWords(field: string): string {
	return field
		.replace(/[_-]+/g, ' ')
		.replace(/([a-z0-9])([A-Z])/g, '$1 $2')
		.replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
		.replace(/\s+/g, ' ')
		.trim()
		.toLowerCase();
}

/** Plumbing the landlord never asked about: timestamps, internal record numbers, row bookkeeping. */
const HIDDEN = [
	/(?:^|\s)at utc$/,
	/(?:^|\s)(?:created|updated) at$/,
	/(?:^|\s)id$/,
	/^row version$/,
	/^concurrency stamp$/,
];

/** Database words → landlord words, applied in order. */
const WORDS: Array<[RegExp, string]> = [
	[/\bpossession agreement\b/g, 'lease'],
	[/\blease agreement\b/g, 'lease'],
	[/\bagreement\b/g, 'lease'],
	[/\bexception reason\b/g, 'override reason'],
	[/\bending disposition\b/g, 'how the lease ended'],
	[/\blifecycle\b/g, 'status'],
	[/\butc\b/g, ''],
];

/**
 * The label to show for one changed field, or `null` when the change should not be shown at all.
 */
export function auditChangeLabel(field: string): string | null {
	const words = toWords(field ?? '');
	if (!words) return null;
	if (HIDDEN.some((pattern) => pattern.test(words))) return null;

	let label = words;
	for (const [pattern, replacement] of WORDS) {
		label = label.replace(pattern, replacement);
	}
	label = label.replace(/\s+/g, ' ').trim();
	if (!label) return null;
	return label.charAt(0).toUpperCase() + label.slice(1);
}

/** The same plain-English swap for an entry's headline, which the API also sends pre-worded. */
export function auditEntryTitle(title: string): string {
	return (title ?? '')
		.replace(/tenant and lease relationship/gi, 'lease participant')
		.replace(/lease agreement/gi, 'lease');
}
