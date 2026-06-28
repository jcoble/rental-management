import type { ScanDocType } from './scan-context';

export interface ScanUploadCopy {
	title: string;
	helperText: string;
}

export interface ScanProcessingCopy {
	title: string;
	body: string;
}

const DEFAULT_HELPER = 'or click to browse — one PDF or multiple photos accepted';

const UPLOAD_COPY: Record<ScanDocType, ScanUploadCopy> = {
	Expense: {
		title: 'Drop a receipt or invoice here',
		helperText: DEFAULT_HELPER
	},
	Payment: {
		title: 'Drop a rent check or payment receipt here',
		helperText: DEFAULT_HELPER
	},
	WorkOrder: {
		title: 'Drop a maintenance request, estimate, or repair photo here',
		helperText: DEFAULT_HELPER
	},
	Lease: {
		title: 'Drop a lease agreement here',
		helperText: DEFAULT_HELPER
	},
	Application: {
		title: 'Drop a rental application here',
		helperText: DEFAULT_HELPER
	},
	Loan: {
		title: 'Drop a mortgage statement or closing disclosure here',
		helperText: DEFAULT_HELPER
	}
};

const PROCESSING_COPY: Record<ScanDocType, ScanProcessingCopy> = {
	Expense: {
		title: 'Reading your receipt or invoice…',
		body: 'The computer is pulling out vendor, amounts, and dates for you. This usually takes just a few seconds.'
	},
	Payment: {
		title: 'Reading your payment document…',
		body: 'The computer is pulling out payer, amount, check, and date details for you. This usually takes just a few seconds.'
	},
	WorkOrder: {
		title: 'Reading your maintenance request…',
		body: 'The computer is pulling out repair details, priority, and property context for you. This usually takes just a few seconds.'
	},
	Lease: {
		title: 'Reading your lease…',
		body: 'The computer is pulling out parties, rent, dates, and lease terms for you. This usually takes just a few seconds.'
	},
	Application: {
		title: 'Reading your rental application…',
		body: 'The computer is pulling out applicant, income, and requested-home details for you. This usually takes just a few seconds.'
	},
	Loan: {
		title: 'Reading your mortgage statement…',
		body: 'The computer is pulling out lender, balance, rate, and payment details for you. This usually takes just a few seconds.'
	}
};

function normalizeDocType(type: ScanDocType | string | null | undefined): ScanDocType {
	return type === 'Payment' || type === 'WorkOrder' || type === 'Lease' || type === 'Application' || type === 'Loan'
		? type
		: 'Expense';
}

export function scanUploadCopy(type: ScanDocType | string | null | undefined): ScanUploadCopy {
	return UPLOAD_COPY[normalizeDocType(type)];
}

export function scanProcessingCopy(type: ScanDocType | string | null | undefined): ScanProcessingCopy {
	return PROCESSING_COPY[normalizeDocType(type)];
}
