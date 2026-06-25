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

const EXPENSE_CATEGORY_ALIASES = new Map([
	['auto travel', 'AutoTravel'],
	['auto and travel', 'AutoTravel'],
	['cleaning maintenance', 'CleaningMaintenance'],
	['cleaning and maintenance', 'CleaningMaintenance'],
	['legal professional', 'LegalProfessional'],
	['legal and professional fees', 'LegalProfessional'],
	['management fees', 'ManagementFees'],
	['mortgage interest', 'MortgageInterest'],
	['repairs maintenance', 'Repairs'],
	['repairs and maintenance', 'Repairs'],
	['repair maintenance', 'Repairs']
]);

const PROPERTY_REFERENCE_PATTERNS = [
	/\bproperty\s*:\s*([^.;\n]+)/i,
	/\bproperty\s+([A-Za-z0-9][^.;\n]+)/i
];

const LEASE_REFERENCE_PATTERNS = [
	/\blease\s+(?:reference|number|no\.?|#)\s*:?\s*([A-Za-z0-9][A-Za-z0-9._/-]*)/i,
	/\blease\s*:?\s*([A-Za-z0-9][A-Za-z0-9._/-]*)/i
];

const PAYMENT_PREFERRED_LEASE_STATUSES = new Set(['Draft', 'PendingSignature', 'Active']);

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

function normalizeOptionText(value: string | undefined | null): string {
	return (value ?? '')
		.trim()
		.toLowerCase()
		.replace(/&/g, ' and ')
		.replace(/[^a-z0-9]+/g, ' ')
		.replace(/\s+/g, ' ')
		.trim();
}

export function scanCategoryValue(value: string | undefined | null, targetEntityType: string | null | undefined): string {
	if (!value) return '';

	const options = scanCategoryOptionsForTarget(targetEntityType);
	const direct = options.find((option) => option.value.toLowerCase() === value.toLowerCase());
	if (direct) return direct.value;

	const normalized = normalizeOptionText(value);
	const labelMatch = options.find((option) => normalizeOptionText(option.label) === normalized);
	if (labelMatch) return labelMatch.value;

	if (targetEntityType !== 'WorkOrder') {
		const alias = EXPENSE_CATEGORY_ALIASES.get(normalized);
		if (alias) return alias;
	}

	return value;
}

export function scanCategoryLabel(value: string | undefined | null, targetEntityType: string | null | undefined): string {
	if (!value) return 'Select category';

	const labels = targetEntityType === 'WorkOrder' ? WORK_ORDER_CATEGORY_LABELS : EXPENSE_CATEGORY_LABELS;
	const normalizedValue = scanCategoryValue(value, targetEntityType);
	return labels[normalizedValue] ?? value;
}

export function buildScanReviewInitialEditedFields(
	fields: ScanReviewField[],
	targetEntityType: string | null | undefined
): Record<string, string> {
	const initial: Record<string, string> = {};
	for (const field of fields) {
		if (field.name === LINE_ITEMS_FIELD) continue;
		initial[field.name] = field.name === 'category'
			? scanCategoryValue(field.value, targetEntityType)
			: field.value;
	}
	return initial;
}

export interface ScanReviewPropertyCandidate {
	id: number;
	name: string;
}

export interface ScanReviewPropertyResolutionInput {
	contextPropertyId?: number | null;
	fields: ScanReviewField[];
	properties?: ScanReviewPropertyCandidate[] | null;
}

export interface ScanReviewLeaseCandidate {
	id: number;
	leaseNumber?: string | null;
	propertyId?: number | null;
	unitId?: number | null;
	status?: string | null;
}

export interface ScanReviewLeaseResolutionInput {
	contextLeaseId?: number | null;
	fields: ScanReviewField[];
	leases?: ScanReviewLeaseCandidate[] | null;
}

function fieldString(fields: ScanReviewField[], ...names: string[]): string {
	for (const name of names) {
		const value = fields.find((field) => field.name === name)?.value?.trim();
		if (value) return value;
	}
	return '';
}

function positiveIntegerString(value: string): string | null {
	const parsed = Number(value);
	return Number.isInteger(parsed) && parsed > 0 ? String(parsed) : null;
}

function propertyReferenceFromNotes(notes: string): string {
	for (const pattern of PROPERTY_REFERENCE_PATTERNS) {
		const match = notes.match(pattern);
		if (!match?.[1]) continue;
		return match[1]
			.replace(/\b(?:unit|apt|apartment)\s*(?:#|:)?\s*[A-Za-z0-9-]+\b.*$/i, '')
			.replace(/,\s*$/g, '')
			.trim();
	}
	return '';
}

function exactPropertyMatchId(reference: string, properties: ScanReviewPropertyCandidate[]): string | null {
	const normalizedReference = normalizeOptionText(reference);
	if (!normalizedReference) return null;

	const matches = properties.filter((property) => normalizeOptionText(property.name) === normalizedReference);
	return matches.length === 1 ? String(matches[0].id) : null;
}

function leaseReferenceFromNotes(notes: string): string {
	for (const pattern of LEASE_REFERENCE_PATTERNS) {
		const match = notes.match(pattern);
		if (match?.[1]) return match[1].trim();
	}
	return '';
}

function exactLeaseNumberMatchId(reference: string, leases: ScanReviewLeaseCandidate[]): string | null {
	const normalizedReference = normalizeOptionText(reference);
	if (!normalizedReference) return null;

	const matches = leases.filter((lease) => normalizeOptionText(lease.leaseNumber) === normalizedReference);
	return matches.length === 1 ? String(matches[0].id) : null;
}

function singlePreferredLeaseId(matches: ScanReviewLeaseCandidate[]): string | null {
	if (matches.length === 1) return String(matches[0].id);

	const preferred = matches.filter((lease) => lease.status ? PAYMENT_PREFERRED_LEASE_STATUSES.has(lease.status) : false);
	return preferred.length === 1 ? String(preferred[0].id) : null;
}

export function resolveScanReviewPropertyId(input: ScanReviewPropertyResolutionInput): string | null {
	const properties = input.properties ?? [];
	if (properties.length === 0) return null;

	if (input.contextPropertyId && properties.some((property) => property.id === input.contextPropertyId)) {
		return String(input.contextPropertyId);
	}

	const explicitId = positiveIntegerString(fieldString(input.fields, 'property_id', 'propertyId'));
	if (explicitId && properties.some((property) => String(property.id) === explicitId)) {
		return explicitId;
	}

	const propertyName = fieldString(input.fields, 'property_name', 'propertyName');
	const propertyNameMatch = exactPropertyMatchId(propertyName, properties);
	if (propertyNameMatch) return propertyNameMatch;

	const notesProperty = propertyReferenceFromNotes(fieldString(input.fields, 'notes'));
	return exactPropertyMatchId(notesProperty, properties);
}

export function resolveScanReviewLeaseIdFromFields(input: ScanReviewLeaseResolutionInput): string | null {
	const leases = input.leases ?? [];
	if (leases.length === 0) return null;

	if (input.contextLeaseId && leases.some((lease) => lease.id === input.contextLeaseId)) {
		return String(input.contextLeaseId);
	}

	const explicitLeaseId = positiveIntegerString(fieldString(input.fields, 'lease_id', 'leaseId'));
	if (explicitLeaseId && leases.some((lease) => String(lease.id) === explicitLeaseId)) {
		return explicitLeaseId;
	}

	const leaseNumber = fieldString(input.fields, 'lease_number', 'leaseNumber')
		|| leaseReferenceFromNotes(fieldString(input.fields, 'notes'));
	const leaseNumberMatch = exactLeaseNumberMatchId(leaseNumber, leases);
	if (leaseNumberMatch) return leaseNumberMatch;

	const propertyId = positiveIntegerString(fieldString(input.fields, 'property_id', 'propertyId'));
	const unitId = positiveIntegerString(fieldString(input.fields, 'unit_id', 'unitId'));
	if (propertyId && unitId) {
		return singlePreferredLeaseId(
			leases.filter((lease) => String(lease.propertyId) === propertyId && String(lease.unitId) === unitId)
		);
	}

	if (unitId) {
		return singlePreferredLeaseId(leases.filter((lease) => String(lease.unitId) === unitId));
	}

	return null;
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
