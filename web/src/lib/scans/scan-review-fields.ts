export interface ScanReviewField {
	name: string;
	value: string;
	confidence: number;
}

export interface ScanReviewFieldGroup {
	label: string;
	fields: ScanReviewField[];
}

export interface ScanCategoryOption {
	value: string;
	label: string;
}

// ScheduleECategory enum values (mirrors RentalCommand.Core.Enums.ScheduleECategory).
// The submitted value stays the enum name; only the label shown to the landlord is friendly.
export const EXPENSE_SCAN_CATEGORY_OPTIONS: ScanCategoryOption[] = [
	{ value: 'Advertising', label: 'Advertising' },
	{ value: 'AutoTravel', label: 'Auto & travel' },
	{ value: 'CleaningMaintenance', label: 'Cleaning & maintenance' },
	{ value: 'Commissions', label: 'Commissions' },
	{ value: 'Insurance', label: 'Insurance' },
	{ value: 'LegalProfessional', label: 'Legal & professional fees' },
	{ value: 'ManagementFees', label: 'Management fees' },
	{ value: 'MortgageInterest', label: 'Mortgage interest' },
	{ value: 'Repairs', label: 'Repairs & maintenance' },
	{ value: 'Supplies', label: 'Supplies' },
	{ value: 'Taxes', label: 'Taxes' },
	{ value: 'Utilities', label: 'Utilities' },
	{ value: 'Depreciation', label: 'Depreciation' },
	{ value: 'Other', label: 'Other' }
];

export const WORK_ORDER_SCAN_CATEGORY_OPTIONS: ScanCategoryOption[] = [
	{ value: 'General', label: 'General' },
	{ value: 'Plumbing', label: 'Plumbing' },
	{ value: 'Electrical', label: 'Electrical' },
	{ value: 'HVAC', label: 'HVAC' },
	{ value: 'Appliance', label: 'Appliance' },
	{ value: 'Repairs', label: 'Repairs' },
	{ value: 'Roofing', label: 'Roofing' },
	{ value: 'Pest', label: 'Pest' },
	{ value: 'Safety', label: 'Safety' },
	{ value: 'Landscaping', label: 'Landscaping' },
	{ value: 'Cleaning', label: 'Cleaning' },
	{ value: 'Other', label: 'Other' }
];

const EXPENSE_CATEGORY_LABELS: Record<string, string> = Object.fromEntries(
	EXPENSE_SCAN_CATEGORY_OPTIONS.map((category) => [category.value, category.label])
);

const WORK_ORDER_CATEGORY_LABELS: Record<string, string> = Object.fromEntries(
	WORK_ORDER_SCAN_CATEGORY_OPTIONS.map((category) => [category.value, category.label])
);

const LINE_ITEMS_FIELD = 'line_items';

const EXPENSE_FIELD_GROUPS: { label: string; fields: string[] }[] = [
	{
		label: 'Vendor',
		fields: ['vendor_name', 'vendor_address', 'vendor_phone', 'vendor_website', 'vendor_tax_id', 'receipt_number']
	},
	{
		label: 'Amounts',
		fields: ['subtotal', 'tax', 'tax_rate', 'tip', 'discount', 'shipping', 'total', 'payment_method', 'card_last4', 'due_date']
	},
	{
		label: 'Details',
		fields: ['document_kind', 'category', 'transaction_date', 'notes']
	}
];

const PAYMENT_FIELD_GROUPS: { label: string; fields: string[] }[] = [
	{
		label: 'Payment',
		fields: ['total', 'payment_method', 'payer_name', 'bank_name', 'check_number', 'transaction_date']
	},
	{
		label: 'Details',
		fields: ['document_kind', 'notes']
	}
];

const WORK_ORDER_FIELD_GROUPS: { label: string; fields: string[] }[] = [
	{
		label: 'Work order',
		fields: ['title', 'priority', 'description', 'category', 'estimated_cost']
	},
	{
		label: 'Notes',
		fields: ['notes']
	}
];

const INTERNAL_LINK_FIELDS = new Set([
	'property_id',
	'propertyId',
	'unit_id',
	'unitId',
	'tenant_id',
	'tenantId',
	'lease_id',
	'leaseId',
	'vendor_id',
	'vendorId',
	'target_entity_type',
	'targetEntityType'
]);

const WORK_ORDER_HIDDEN_FIELDS = new Set(['transcript']);
const EXPENSE_HIDDEN_FIELDS = new Set(['bank_name', 'payer_name', 'check_number']);
const PAYMENT_HIDDEN_FIELDS = new Set([
	'vendor_name',
	'vendor_address',
	'vendor_phone',
	'vendor_website',
	'vendor_tax_id',
	'receipt_number',
	'subtotal',
	'tax',
	'tax_rate',
	'tip',
	'discount',
	'shipping',
	'card_last4',
	'due_date',
	'category'
]);

const FIELD_LABELS: Record<string, string> = {
	card_last4: 'Card last 4',
	document_kind: 'Document kind',
	due_date: 'Due date',
	estimated_cost: 'Estimated cost',
	payer_name: 'Payer name',
	payment_method: 'Payment method',
	receipt_number: 'Receipt number',
	tax_rate: 'Tax rate',
	transaction_date: 'Transaction date',
	vendor_address: 'Vendor address',
	vendor_name: 'Vendor name',
	vendor_phone: 'Vendor phone',
	vendor_tax_id: 'Vendor tax ID',
	vendor_website: 'Vendor website'
};

export function fieldDisplayLabel(name: string): string {
	const explicit = FIELD_LABELS[name];
	if (explicit) return explicit;

	return name
		.replace(/_/g, ' ')
		.replace(/\bid\b/gi, 'ID')
		.replace(/\b\w/g, (match) => match.toUpperCase());
}

export function shouldShowScanReviewField(fieldName: string, targetEntityType: string | null | undefined): boolean {
	if (fieldName === LINE_ITEMS_FIELD) return false;
	if (INTERNAL_LINK_FIELDS.has(fieldName)) return false;
	if (targetEntityType === 'WorkOrder' && WORK_ORDER_HIDDEN_FIELDS.has(fieldName)) return false;
	if (targetEntityType === 'Payment' && PAYMENT_HIDDEN_FIELDS.has(fieldName)) return false;
	if (targetEntityType === 'Expense' && EXPENSE_HIDDEN_FIELDS.has(fieldName)) return false;
	return true;
}

function reviewFieldGroupsForTarget(targetEntityType: string | null | undefined): { label: string; fields: string[] }[] {
	if (targetEntityType === 'WorkOrder') return WORK_ORDER_FIELD_GROUPS;
	if (targetEntityType === 'Payment') return PAYMENT_FIELD_GROUPS;
	return EXPENSE_FIELD_GROUPS;
}

export function scanCategoryOptionsForTarget(targetEntityType: string | null | undefined): ScanCategoryOption[] {
	if (targetEntityType === 'WorkOrder') return WORK_ORDER_SCAN_CATEGORY_OPTIONS;
	return EXPENSE_SCAN_CATEGORY_OPTIONS;
}

export function scanCategoryLabel(value: string | undefined | null, targetEntityType: string | null | undefined): string {
	if (!value) return 'Select category';

	const labels = targetEntityType === 'WorkOrder' ? WORK_ORDER_CATEGORY_LABELS : EXPENSE_CATEGORY_LABELS;
	return labels[value] ?? value;
}

export function buildScanReviewFieldGroups(
	fields: ScanReviewField[],
	targetEntityType: string | null | undefined
): ScanReviewFieldGroup[] {
	const configuredGroups = reviewFieldGroupsForTarget(targetEntityType);
	const knownFields = new Set(configuredGroups.flatMap((group) => group.fields));
	const fieldMap = new Map(
		fields
			.filter((field) => shouldShowScanReviewField(field.name, targetEntityType))
			.map((field) => [field.name, field])
	);
	const result: ScanReviewFieldGroup[] = [];

	for (const group of configuredGroups) {
		const present = group.fields
			.map((name) => fieldMap.get(name))
			.filter((field): field is ScanReviewField => !!field);
		if (present.length > 0) {
			result.push({ label: group.label, fields: present });
		}
	}

	const otherFields = [...fieldMap.values()].filter((field) => !knownFields.has(field.name));
	if (otherFields.length > 0) {
		result.push({ label: 'Other', fields: otherFields });
	}

	return result;
}
