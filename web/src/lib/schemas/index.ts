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
/** Required email field — non-empty and valid format. */
const requiredEmail = (label: string) =>
	z
		.string()
		.trim()
		.min(1, `${label} is required`)
		.email(`${label} must be a valid email address`);
const numericString = (label: string) =>
	z
		.string()
		.trim()
		.min(1, `${label} is required`)
		.refine((v) => !Number.isNaN(Number(v)), `${label} must be a number`)
		.transform((v) => Number(v));
const optionalNumericString = (label: string) =>
	z
		.string()
		.trim()
		.transform((v) => (v.length ? v : null))
		.nullable()
		.refine((v) => v === null || !Number.isNaN(Number(v)), `${label} must be a number`)
		.transform((v) => (v === null ? null : Number(v)));
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
/**
 * Required numeric field that must be > 0 (use for money amounts that represent
 * a real charge: monthly rent, payment amount, etc.).
 */
const positiveNumeric = (label: string) =>
	z
		.string()
		.trim()
		.min(1, `${label} is required`)
		.refine((v) => !Number.isNaN(Number(v)), `${label} must be a number`)
		.transform((v) => Number(v))
		.refine((v) => v > 0, `${label} must be greater than zero`);
/**
 * Required numeric field that must be >= 0 (use for counts/amounts that may
 * legitimately be zero: bedrooms, bathrooms, market rent on a vacant unit, fees).
 */
const nonNegativeNumeric = (label: string) =>
	z
		.string()
		.trim()
		.min(1, `${label} is required`)
		.refine((v) => !Number.isNaN(Number(v)), `${label} must be a number`)
		.transform((v) => Number(v))
		.refine((v) => v >= 0, `${label} cannot be negative`);
/**
 * Optional numeric field that must be >= 0 when provided.
 * '' → null; non-numeric or negative → validation error.
 */
const optionalNonNegative = (label: string) =>
	z
		.string()
		.trim()
		.transform((v) => (v.length ? Number(v) : null))
		.refine(
			(v) => v === null || (!Number.isNaN(v) && v >= 0),
			`${label} must be a non-negative number`
		);

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
	// bedrooms/bathrooms: server [Range(0, 99)] — non-negative
	bedrooms: nonNegativeNumeric('Bedrooms'),
	bathrooms: nonNegativeNumeric('Bathrooms'),
	// marketRent: server [Range(0, 99999999)] — can be 0 for a vacant/unlisted unit
	marketRent: nonNegativeNumeric('Market rent'),
});

export const tenantSchema = z.object({
	firstName: required('First name'),
	lastName: required('Last name'),
	email: optionalEmail,
	phone: optionalText,
	emergencyContact: optionalText,
});

export const leaseSchema = z.object({
	leaseNumber: required('Lease number'),
	propertyId: numericString('Property'),
	unitId: numericString('Unit'),
	tenantId: numericString('Tenant'),
	startDate: required('Start date'),
	endDate: required('End date'),
	// monthlyRent: server [Range(0.01, 99999999)] — must be > 0
	monthlyRent: positiveNumeric('Monthly rent'),
	// securityDeposit: server [Range(0, 99999999)] — can be 0
	securityDeposit: nonNegativeNumeric('Security deposit'),
	// lateFeeAmount: server [Range(0, 99999999)] — can be 0
	lateFeeAmount: nonNegativeNumeric('Late fee'),
	// rentDueDay: server [Range(1, 31)]
	rentDueDay: numericString('Due day').refine((v) => v >= 1 && v <= 31, 'Due day must be between 1 and 31'),
	status: z.string(),
	notes: optionalText,
});

export const paymentSchema = z.object({
	leaseId: numericString('Lease'),
	// amount: server [Range(0.01, 99999999)] — must be > 0
	amount: positiveNumeric('Amount'),
	dueDate: required('Due date'),
	paymentType: z.string(),
	status: z.string(),
	paidDate: optionalText,
	method: optionalText,
	externalReference: optionalText,
	notes: optionalText,
});

export const expenseSchema = z.object({
	description: required('Description'),
	// amount: server [Range(0, 99999999)] — expenses can legitimately be $0
	amount: nonNegativeNumeric('Amount'),
	subtotal: optionalNonNegative('Subtotal'),
	taxAmount: optionalNonNegative('Tax'),
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
	taxRate: optionalNonNegative('Tax rate').refine(
		(v) => v === null || v <= 100,
		'Tax rate cannot exceed 100%'
	),
	tip: optionalNonNegative('Tip'),
	discount: optionalNonNegative('Discount'),
	shipping: optionalNonNegative('Shipping'),
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
	// Optional smart-checklist template. Built-in templates have negative ids — kept as-is.
	templateId: idString,
	inspector: optionalText,
});

/**
 * Recurring maintenance task ("HVAC filter every 90 days"). Property + title +
 * next-due date are required; unit/vendor are optional ids that blank → null.
 */
export const recurringMaintenanceSchema = z.object({
	propertyId: numericString('Property'),
	title: required('Title'),
	description: optionalText,
	category: optionalText,
	unitId: idString,
	vendorId: idString,
	recurrenceInterval: z.string(),
	nextDueDate: required('Next due date'),
	priority: z.string(),
	isActive: z.boolean(),
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
	// prospectEmail: server [EmailAddress] optional
	prospectEmail: optionalEmail,
	assignedTo: optionalText,
});

export const ownerSchema = z.object({
	name: required('Name'),
	ownerEntityType: z.string(),
	taxId: optionalText,
	address: optionalText,
	phone: optionalText,
	// email: server [EmailAddress] optional
	email: optionalEmail,
});

export const vendorSchema = z.object({
	name: required('Name'),
	serviceType: required('Service type'),
	// email: server [EmailAddress] optional
	email: optionalEmail,
	phone: optionalText,
	is1099Eligible: z.boolean(),
	w9OnFile: z.boolean(),
	preferred: z.boolean(),
});

/**
 * New security deposit holding: lease is required; amount is optional (defaults
 * to the lease's deposit amount server-side) but if provided must be > 0.
 */
export const newDepositHoldingSchema = z.object({
	leaseId: numericString('Lease'),
	// amount is optional — blank means "use the lease default"
	amount: optionalNonNegative('Amount'),
	notes: optionalText,
});

/** Deduction to add to an existing deposit holding. */
export const depositDeductionSchema = z.object({
	reason: required('Reason'),
	// deduction amounts must be > 0
	amount: positiveNumeric('Amount'),
	notes: optionalText,
});

/** Portfolio settings update: only name is required on the server. */
export const settingsSchema = z.object({
	name: required('Portfolio name'),
	description: optionalText,
	managementCompanyName: optionalText,
	timeZone: optionalText,
	status: z.string(),
	settings: optionalText,
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
