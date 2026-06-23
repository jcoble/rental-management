export interface ScanReviewField {
	name: string;
	value: string;
	confidence: number;
}

export interface ScanReviewFieldGroup {
	label: string;
	fields: ScanReviewField[];
}

const LINE_ITEMS_FIELD = 'line_items';

const COMMON_FIELD_GROUPS: { label: string; fields: string[] }[] = [
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
		fields: ['document_kind', 'category', 'notes']
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

const FIELD_LABELS: Record<string, string> = {
	card_last4: 'Card last 4',
	document_kind: 'Document kind',
	due_date: 'Due date',
	estimated_cost: 'Estimated cost',
	payment_method: 'Payment method',
	receipt_number: 'Receipt number',
	tax_rate: 'Tax rate',
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
	return true;
}

export function buildScanReviewFieldGroups(
	fields: ScanReviewField[],
	targetEntityType: string | null | undefined
): ScanReviewFieldGroup[] {
	const configuredGroups = targetEntityType === 'WorkOrder'
		? WORK_ORDER_FIELD_GROUPS
		: COMMON_FIELD_GROUPS;
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
