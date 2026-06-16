/**
 * Zod form schemas for the rental entities. Each schema validates the raw
 * (string-bound) form state; transforms coerce numeric/optional fields into the
 * shape the API expects. {@link parseForm} runs a schema and returns either the
 * parsed payload or a flat field->message error map for inline display.
 */

import { z } from 'zod';

const required = (label: string) => z.string().trim().min(1, `${label} is required`);
// Optional helpers end in `.optional()` so a MISSING key (`undefined`) is tolerated,
// not just an explicit null. Without it, any form that validates a subset of its
// schema (a detail/edit form, or one that drops a field before validating) errors on
// the absent keys and Save silently bails. A present '' still becomes null.
const optionalText = z
	.string()
	.trim()
	.transform((v) => (v.length ? v : null))
	.nullable()
	.optional();
const optionalEmail = z
	.string()
	.trim()
	.transform((v) => (v.length ? v : null))
	.nullable()
	.refine((v) => v === null || z.string().email().safeParse(v).success, 'Enter a valid email')
	.optional();
/** Required email field — non-empty and valid format. */
const requiredEmail = (label: string) =>
	z
		.string()
		.trim()
		.min(1, `${label} is required`)
		.email(`${label} must be a valid email address`);
/**
 * Numeric inputs are bound to form state as strings, but Svelte 5 coerces
 * `bind:value` on an `<input type="number">` to an actual `number` (even when the
 * underlying `$state` was initialised as `''`). By submit time a numeric field can
 * therefore be a `number`, not the `string` the string-first helpers below expect —
 * which made Zod throw `expected string, received number` and block the whole form
 * (TSK-195). Coerce a `number` (and `null`/`undefined`) back to a string here so the
 * existing string-first chain works regardless of input type. This immunises every
 * numeric form field at one place; no per-input `type` change required.
 */
const coerceNumericInput = <T extends z.ZodType>(schema: T) =>
	z.preprocess((v) => {
		if (typeof v === 'number') return Number.isFinite(v) ? String(v) : '';
		if (v == null) return '';
		return v;
	}, schema);

const numericString = (label: string) =>
	coerceNumericInput(
		z
			.string()
			.trim()
			.min(1, `${label} is required`)
			.refine((v) => !Number.isNaN(Number(v)), `${label} must be a number`)
			.transform((v) => Number(v))
	);
const optionalNumericString = (label: string) =>
	coerceNumericInput(
		z
			.string()
			.trim()
			.transform((v) => (v.length ? v : null))
			.nullable()
			.refine((v) => v === null || !Number.isNaN(Number(v)), `${label} must be a number`)
			.transform((v) => (v === null ? null : Number(v)))
			.optional()
	);
const idString = coerceNumericInput(
	z
		.string()
		.trim()
		.transform((v) => (v.length ? Number(v) : null))
		.nullable()
		.optional()
);
// Optional money/number: '' -> null, otherwise coerced to a number (rejects non-numeric).
const optionalNumeric = (label: string) =>
	coerceNumericInput(
		z
			.string()
			.trim()
			.transform((v) => (v.length ? Number(v) : null))
			.refine((v) => v === null || !Number.isNaN(v), `${label} must be a number`)
			.optional()
	);
/**
 * Required numeric field that must be > 0 (use for money amounts that represent
 * a real charge: monthly rent, payment amount, etc.).
 */
const positiveNumeric = (label: string) =>
	coerceNumericInput(
		z
			.string()
			.trim()
			.min(1, `${label} is required`)
			.refine((v) => !Number.isNaN(Number(v)), `${label} must be a number`)
			.transform((v) => Number(v))
			.refine((v) => v > 0, `${label} must be greater than zero`)
	);
/**
 * Required numeric field that must be >= 0 (use for counts/amounts that may
 * legitimately be zero: bedrooms, bathrooms, market rent on a vacant unit, fees).
 */
const nonNegativeNumeric = (label: string) =>
	coerceNumericInput(
		z
			.string()
			.trim()
			.min(1, `${label} is required`)
			.refine((v) => !Number.isNaN(Number(v)), `${label} must be a number`)
			.transform((v) => Number(v))
			.refine((v) => v >= 0, `${label} cannot be negative`)
	);
/**
 * Optional numeric field that must be >= 0 when provided.
 * '' → null; non-numeric or negative → validation error.
 */
const optionalNonNegative = (label: string) =>
	coerceNumericInput(
		z
			.string()
			.trim()
			.transform((v) => (v.length ? Number(v) : null))
			.refine(
				(v) => v === null || (!Number.isNaN(v) && v >= 0),
				`${label} must be a non-negative number`
			)
			.optional()
	);

export const propertySchema = z.object({
	name: required('Name'),
	type: z.string(),
	status: z.string(),
	addressLine1: required('Address'),
	addressLine2: optionalText,
	city: required('City'),
	state: required('State'),
	postalCode: required('ZIP'),
	// The property's owner is an OwnerEntity (the API validates ownerEntityId against OwnerEntities;
	// ownerId is the legacy Owners table). owners.list() returns OwnerEntities, so this is their id.
	ownerEntityId: idString,
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
	// Move-in date is distinct from lease start and optional (nullable on the entity);
	// blank clears it and round-trips as null via UpdateLeaseRequest.MoveInDate.
	moveInDate: optionalText,
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
	// amount: server [Range(0.01, 99999999)] (ExpenseDtos.cs Create/UpdateExpenseRequest.Amount)
	// — must be > 0. Previously used nonNegativeNumeric (allowed 0), which passed client
	// validation then 400'd at the server; positiveNumeric surfaces the error inline instead.
	amount: positiveNumeric('Amount'),
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
		(v) => v == null || v <= 100,
		'Tax rate cannot exceed 100%'
	),
	tip: optionalNonNegative('Tip'),
	discount: optionalNonNegative('Discount'),
	shipping: optionalNonNegative('Shipping'),
});

/**
 * The expense DETAIL page edits only a subset of the expense fields — the rest
 * (vendor contact, payment method, card last 4, tip/discount/shipping/tax-rate,
 * receipt/work-order refs) live in receiptData and are only set via the scan/create
 * flow. Validating the detail form against the full {@link expenseSchema} errored on
 * those absent keys (zod `.nullable()` rejects `undefined`, a missing key), so the
 * form never validated and Save silently bailed. Pick exactly the keys the detail
 * form carries; the PATCH-style update API leaves any field not sent untouched.
 */
export const expenseDetailSchema = expenseSchema.pick({
	description: true,
	amount: true,
	subtotal: true,
	taxAmount: true,
	incurredAt: true,
	dueDate: true,
	paidAt: true,
	category: true,
	status: true,
	propertyId: true,
	vendorId: true,
	billableToOwner: true,
	notes: true
});

export const workOrderSchema = z.object({
	propertyId: numericString('Property'),
	title: required('Title'),
	description: required('Description'),
	priority: z.string(),
	category: required('Category'),
	// Optional maintenance context (all blank → null). unitId is filtered to the chosen property
	// in the UI; scheduledFor + scheduledWindowEnd are `datetime-local` strings bracketing the
	// arrival window (the API pins them to UTC); estimatedCost is optional non-negative money.
	unitId: idString,
	tenantId: idString,
	vendorId: idString,
	scheduledFor: optionalText,
	scheduledWindowEnd: optionalText,
	estimatedCost: optionalNonNegative('Estimated cost'),
});

/**
 * Work-order DETAIL edit form: the core Request fields plus the editable Costs & timing
 * (requested / scheduled / completed dates + estimated / actual cost). Dates are bound as
 * `yyyy-MM-dd` strings (DatePicker) and pass straight through — the API pins them to UTC and
 * treats a null/missing value as "leave unchanged" (the work order detail page never clears a
 * date back to empty, matching the nullable-means-untouched PATCH semantics). Costs are optional
 * non-negative money. Mirrors the {@link expenseDetailSchema} pattern.
 */
export const workOrderDetailSchema = workOrderSchema.extend({
	requestedAt: optionalText,
	scheduledFor: optionalText,
	completedAt: optionalText,
	estimatedCost: optionalNonNegative('Estimated cost'),
	actualCost: optionalNonNegative('Actual cost'),
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
	addressLine1: optionalText,
	addressLine2: optionalText,
	city: optionalText,
	state: optionalText,
	postalCode: optionalText,
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
