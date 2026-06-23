import type { UnitDocumentSummary } from '$lib/types';

export function unitDocumentHref(
	doc: Pick<UnitDocumentSummary, 'id'>
): string | null {
	return doc.id > 0 ? `/document-file/${doc.id}` : null;
}
