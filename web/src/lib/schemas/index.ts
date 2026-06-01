/**
 * Zod form schemas for the rental entities. Each schema validates the raw
 * (string-bound) form state; transforms coerce numeric/optional fields into the
 * shape the API expects. {@link parseForm} runs a schema and returns either the
 * parsed payload or a flat field->message error map for inline display.
 */

import { z } from 'zod';

const required = (label: string) => z.string().trim().min(1, `${label} is required`);
const optionalText = z
	.string()
	.trim()
	.transform((v) => (v.length ? v : null))
	.nullable();
const optionalEmail = z
	.string()
	.trim()
	.transform((v) => (v.length ? v : null))
	.nullable()
	.refine((v) => v === null || z.string().email().safeParse(v).success, 'Enter a valid email');
const numericString = (label: string) =>
	z
		.string()
		.trim()
		.min(1, `${label} is required`)
		.refine((v) => !Number.isNaN(Number(v)), `${label} must be a number`)
		.transform((v) => Number(v));
const idString = z
	.string()
	.trim()
	.transform((v) => (v.length ? Number(v) : null))
	.nullable();
// Optional money/number: '' -> null, otherwise coerced to a number (rejects non-numeric).
const optionalNumeric = (label: string) =>
	z
		.string()
		.trim()
		.transform((v) => (v.length ? Number(v) : null))
		.refine((v) => v === null || !Number.isNaN(v), `${label} must be a number`);

export const propertySchema = z.object({
	name: required('Name'),
	type: z.string(),
	addressLine1: required('Address'),
	city: required('City'),
	state: required('State'),
	postalCode: required('ZIP'),
	ownerId: idString,
});

export const unitSchema = z.object({
	unitNumber: required('Unit number'),
	bedrooms: numericString('Bedrooms'),
	bathrooms: numericString('Bathrooms'),
	marketRent: numericString('Market rent'),
});

export const tenantSchema = z.object({
	firstName: required('First name'),
	lastName: required('Last name'),
	email: optionalEmail,
	phone: optionalText,
	emergencyContact: optionalText,
});

export const leaseSchema = z.object({
	unitId: numericString('Unit'),
	tenantId: numericString('Tenant'),
	startDate: required('Start date'),
	endDate: required('End date'),
	monthlyRent: numericString('Monthly rent'),
	securityDeposit: numericString('Security deposit'),
	lateFeeAmount: numericString('Late fee'),
	rentDueDay: numericString('Due day'),
	status: z.string(),
});

export const paymentSchema = z.object({
	leaseId: numericString('Lease'),
	amount: numericString('Amount'),
	dueDate: required('Due date'),
	type: z.string(),
	status: z.string(),
});

export const expenseSchema = z.object({
	description: required('Description'),
	amount: numericString('Amount'),
	subtotal: optionalNumeric('Subtotal'),
	taxAmount: optionalNumeric('Tax'),
	incurredAt: required('Incurred date'),
	dueDate: optionalText,
	paidAt: optionalText,
	category: z.string(),
	status: z.string(),
	propertyId: idString,
	vendorId: idString,
	workOrderId: idString,
	billableToOwner: z.boolean(),
	notes: optionalText,
	// Receipt detail (the accounting page nests these into receiptData JSON before submit).
	vendorAddress: optionalText,
	vendorPhone: optionalText,
	vendorWebsite: optionalText,
	vendorTaxId: optionalText,
	receiptNumber: optionalText,
	paymentMethod: optionalText,
	cardLast4: optionalText,
	taxRate: optionalNumeric('Tax rate'),
	tip: optionalNumeric('Tip'),
	discount: optionalNumeric('Discount'),
	shipping: optionalNumeric('Shipping'),
});

export const workOrderSchema = z.object({
	propertyId: numericString('Property'),
	title: required('Title'),
	description: required('Description'),
	priority: z.string(),
	category: required('Category'),
});

export const inspectionSchema = z.object({
	propertyId: numericString('Property'),
	type: z.string(),
	scheduledFor: required('Scheduled date'),
});

export const appointmentSchema = z.object({
	title: required('Title'),
	type: z.string(),
	status: z.string(),
	scheduledStart: required('Start time'),
	scheduledEnd: optionalText,
	propertyId: idString,
	tenantId: idString,
	prospectName: optionalText,
	prospectEmail: optionalEmail,
	assignedTo: optionalText,
});

export const ownerSchema = z.object({
	name: required('Name'),
	ownerEntityType: z.string(),
	taxId: optionalText,
	address: optionalText,
	phone: optionalText,
	email: optionalEmail,
});

export const vendorSchema = z.object({
	name: required('Name'),
	serviceType: required('Service type'),
	email: optionalEmail,
	phone: optionalText,
	is1099Eligible: z.boolean(),
	w9OnFile: z.boolean(),
	preferred: z.boolean(),
});

/**
 * Run a schema over raw form input. On success returns `{ data }`; on failure
 * returns `{ errors }` keyed by field name with the first message per field.
 */
export function parseForm<T extends z.ZodType>(
	schema: T,
	input: unknown
): { data: z.infer<T>; errors: null } | { data: null; errors: Record<string, string> } {
	const result = schema.safeParse(input);
	if (result.success) {
		return { data: result.data, errors: null };
	}
	const errors: Record<string, string> = {};
	for (const issue of result.error.issues) {
		const key = String(issue.path[0] ?? '_');
		if (!errors[key]) errors[key] = issue.message;
	}
	return { data: null, errors };
}
