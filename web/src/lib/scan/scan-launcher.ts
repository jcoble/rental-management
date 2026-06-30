import { appendScanContext, type ScanContext, type ScanDocType } from './scan-context.ts';

const CONTEXTUAL_TITLES: Record<ScanDocType, string> = {
	Expense: 'Scan an expense',
	Payment: 'Scan a payment',
	WorkOrder: 'Scan a work order',
	Lease: 'Scan a lease agreement',
	Application: 'Scan an application',
	Loan: 'Scan a loan document'
};

export type ScanLauncherMode = 'global' | 'contextual';

export function shouldAskForScanDocumentType(context: ScanContext): boolean {
	return !context.type;
}

export function scanLauncherMode(context: ScanContext): ScanLauncherMode {
	return shouldAskForScanDocumentType(context) ? 'global' : 'contextual';
}

export function contextualScanTitle(context: ScanContext): string {
	return context.type ? CONTEXTUAL_TITLES[context.type] : 'Scan a document';
}

export function buildScanReviewTarget(draftId: number, context: ScanContext): string {
	return appendScanContext(`/scan/${draftId}`, context);
}
