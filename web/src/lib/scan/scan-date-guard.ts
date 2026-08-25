import { shouldShowScanReviewField } from './scan-review-fields.ts';

export type ScanDateGuardLoanMode = 'match' | 'create';

export interface ScanDateGuardInput {
	targetEntityType: string | null | undefined;
	loanReviewMode?: ScanDateGuardLoanMode;
	fields?: readonly { name: string }[];
	isProcessing?: boolean;
	status?: string | null;
	isTerminal?: boolean;
}

export const GENERIC_SCAN_DATE_FIELDS = new Set(['due_date', 'transaction_date']);

const LEASE_DATE_FIELDS = new Set(['start_date', 'end_date']);
const APPLICATION_DATE_FIELDS = new Set(['date_of_birth', 'desired_move_in_date']);
const LOAN_MATCH_DATE_FIELDS = new Set(['statement_effective_date']);
const LOAN_CREATE_DATE_FIELDS = new Set(['start_date']);

/** Return only DatePicker fields rendered by the current scan-review branch/mode. */
export function activeScanDateFieldNames(input: ScanDateGuardInput): ReadonlySet<string> {
	if (input.isProcessing || input.status === 'Failed') return new Set();

	switch (input.targetEntityType) {
		case 'LeaseAgreement':
			return LEASE_DATE_FIELDS;
		case 'Application':
			return APPLICATION_DATE_FIELDS;
		case 'Loan':
			return input.loanReviewMode === 'match' && !input.isTerminal
				? LOAN_MATCH_DATE_FIELDS
				: LOAN_CREATE_DATE_FIELDS;
		default: {
			const fields = input.fields ?? [];
			if (fields.length === 0) return new Set(['transaction_date']);
			return new Set(
				fields
					.filter((field) => GENERIC_SCAN_DATE_FIELDS.has(field.name))
					.filter((field) => shouldShowScanReviewField(field.name, input.targetEntityType))
					.map((field) => field.name)
			);
		}
	}
}

export function hasVisibleScanDateInvalid(
	invalidByField: Readonly<Record<string, boolean>>,
	activeFields: ReadonlySet<string>
): boolean {
	for (const fieldName of activeFields) {
		if (invalidByField[fieldName]) return true;
	}
	return false;
}
