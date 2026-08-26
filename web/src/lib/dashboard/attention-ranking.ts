/**
 * Ranking for the dashboard's single "Needs attention" list.
 *
 * The landlord sees one list, money first, because money is the thing that hurts to miss.
 * Everything else follows in the order the owner asked for: repairs, tenant messages,
 * applications waiting on a decision, leases waiting on a signature, then move-ins and
 * move-outs. Pure functions so the order is testable without rendering the page.
 */

export type AttentionKind = 'money' | 'repair' | 'message' | 'application' | 'signature' | 'move';

/** How many rows the dashboard shows before the "See all" button. */
export const ATTENTION_LIMIT = 8;

const KIND_ORDER: readonly AttentionKind[] = [
	'money',
	'repair',
	'message',
	'application',
	'signature',
	'move'
];

/**
 * Sorts by kind (money first) keeping the original order inside each kind, then trims the
 * list to `limit`. Pass a bigger limit to render every row behind "See all".
 */
export function rankAttention<T extends { kind: AttentionKind }>(
	items: readonly T[],
	limit: number = ATTENTION_LIMIT
): T[] {
	return items
		.map((item, index) => ({ item, index }))
		.sort(
			(a, b) =>
				KIND_ORDER.indexOf(a.item.kind) - KIND_ORDER.indexOf(b.item.kind) || a.index - b.index
		)
		.slice(0, Math.max(0, limit))
		.map((entry) => entry.item);
}

/**
 * Which kind a daily-briefing line belongs to. Rent lines return null: the dashboard builds its
 * money rows from the "Who's behind" list instead, because those rows carry the Record payment
 * button. Anything we do not recognise sorts last rather than disappearing.
 */
export function attentionKindForBriefingCategory(category: string): AttentionKind | null {
	switch (category) {
		case 'RentDue':
		case 'RentLate':
			return null;
		case 'Maintenance':
			return 'repair';
		case 'Message':
			return 'message';
		case 'Application':
		case 'RentalApplication':
			return 'application';
		case 'LeaseExpiring':
		case 'Signature':
			return 'signature';
		default:
			return 'move';
	}
}
