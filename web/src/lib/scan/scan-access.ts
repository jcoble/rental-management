import { CAPABILITY } from '../auth/experience-policy.ts';
import { SCAN_DOC_TYPES, type ScanDocType } from './scan-context.ts';

/**
 * The scan launcher is a command surface, not merely navigation. Keep its choices aligned with the
 * exact commands the active experience may complete so a Leasing Agent or Maintenance Technician
 * cannot begin a draft that the API must later reject or route through a management-only command.
 */
export function scanDocumentTypesForCapabilities(
	capabilities: ReadonlySet<string>
): readonly ScanDocType[] {
	const allowed: ScanDocType[] = [];
	if (capabilities.has(CAPABILITY.moneyExpensesManage)) allowed.push('Expense');
	if (capabilities.has(CAPABILITY.moneyPaymentsManage)) allowed.push('Payment');
	if (capabilities.has(CAPABILITY.workManage) || capabilities.has(CAPABILITY.assignedWorkUpdate)) {
		allowed.push('WorkOrder');
	}
	if (capabilities.has(CAPABILITY.rentalsManage) || capabilities.has(CAPABILITY.leasingAgreementsPrepare)) {
		allowed.push('LeaseAgreement');
	}
	if (capabilities.has(CAPABILITY.leasingApplicationsManage)) allowed.push('Application');
	if (capabilities.has(CAPABILITY.moneyExpensesManage)) allowed.push('Loan');
	if (capabilities.has(CAPABILITY.rentalsManage)) allowed.push('LeaseEndingNotice');
	return allowed;
}

export function canUseUnstructuredVoiceCapture(
	allowedTypes: readonly ScanDocType[]
): boolean {
	return SCAN_DOC_TYPES.every((type) => allowedTypes.includes(type));
}
