import type { UnitDocumentSummary } from '$lib/types';

const ENTITY_FILE_ROUTES: Record<string, string> = {
	application: '/application-file',
	expense: '/expense-file',
	lease: '/lease-file',
	payment: '/payment-file',
	scan: '/scan-file',
	scandraft: '/scan-file',
	workorder: '/workorder-file'
};

export function unitDocumentHref(
	doc: Pick<UnitDocumentSummary, 'entityType' | 'entityId'>
): string | null {
	if (!doc.entityType || doc.entityId == null) return null;

	const base = ENTITY_FILE_ROUTES[doc.entityType.toLowerCase()];
	return base ? `${base}/${doc.entityId}` : null;
}
