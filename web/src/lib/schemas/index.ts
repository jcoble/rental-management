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
const optionalUrl = z
	.string()
	.trim()
	.transform((v) => (v.length ? v : null))
	.nullable()
	.refine((v) => v === null || z.string().url().safeParse(v).success, 'Enter a valid URL')
	.optional();
/**
 * Optional free text with an upper length bound mirroring a server MaxLength.
 * Behaves like {@link optionalText} ('' → null, missing key tolerated) but rejects
 * input longer than `max` so the client surfaces it before the server 400s.
 */
const optionalTextMax = (label: string, max: number) =>
	z
		.string()
		.trim()
		.max(max, `${label} must be ${max} characters or fewer`)
		.transform((v) => (v.length ? v : null))
		.nullable()
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

// Lengths mirror the server data annotations on Create/UpdatePropertyRequest (PropertyDtos.cs):
// Name 200, AddressLine1/2 250, City/State 100, PostalCode 20 — so an over-long value is caught
// inline instead of bouncing off a raw 400.
export const propertySchema = z.object({
	name: required('Name').max(200, 'Name must be 200 characters or fewer'),
	type: z.string(),
	status: z.string(),
	addressLine1: required('Address').max(250, 'Address must be 250 characters or fewer'),
	addressLine2: optionalTextMax('Apt / Suite / Unit #', 250),
	city: required('City').max(100, 'City must be 100 characters or fewer'),
	state: required('State').max(100, 'State must be 100 characters or fewer'),
	postalCode: required('ZIP').max(20, 'ZIP must be 20 characters or fewer'),
	// The property's owner is an OwnerEntity (the API validates ownerEntityId against OwnerEntities;
	// ownerId is the legacy Owners table). owners.list() returns OwnerEntities, so this is their id.
	ownerEntityId: idString,
});

export const unitSchema = z.object({
	// unitNumber: server [MaxLength(50)]
	unitNumber: required('Unit number').max(50, 'Unit number must be 50 characters or fewer'),
	// bedrooms/bathrooms: server [Range(0, 99)] — non-negative, max 99
	bedrooms: nonNegativeNumeric('Bedrooms').refine((v) => v <= 99, 'Bedrooms cannot exceed 99'),
	bathrooms: nonNegativeNumeric('Bathrooms').refine((v) => v <= 99, 'Bathrooms cannot exceed 99'),
	// marketRent: server [Range(0, 99999999)] — can be 0 for a vacant/unlisted unit
	marketRent: nonNegativeNumeric('Market rent').refine((v) => v <= 99999999, 'Market rent cannot exceed 99,999,999'),
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

/**
 * Record-application-fee form for the application's pre-tenancy financial account.
 */
export const applicationFeeSchema = z.object({
	amount: positiveNumeric('Amount'),
	method: optionalText,
	effectiveOn: optionalText,
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
	unitId: idString,
	vendorId: idString,
	workOrderId: idString,
	billableToOwner: z.boolean(),
	notes: optionalText,
	// Receipt detail (the accounting page nests these into receiptData JSON before submit).
	vendorAddress: optionalText,
	vendorPhone: optionalText,
	vendorWebsite: optionalUrl,
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
	scheduledTime: optionalText,
	estimatedCost: optionalNonNegative('Estimated cost'),
	priority: z.string(),
	isActive: z.boolean(),
});

// Per-property loan (mortgage). Amounts map to the server's [Range] validators; escrow may be 0.
export const loanSchema = z.object({
	lender: required('Lender'),
	// originalAmount: server [Range(0, …)] — can be 0 for an already-paid-off loan being recorded.
	originalAmount: nonNegativeNumeric('Original amount'),
	currentBalance: optionalNonNegative('Current balance'),
	// annualInterestRatePct: server [Range(0, 100)].
	annualInterestRatePct: nonNegativeNumeric('Interest rate').refine((v) => v <= 100, 'Rate must be 0–100'),
	// termMonths: server [Range(1, 1200)].
	termMonths: numericString('Term (months)').refine((v) => v >= 1 && v <= 1200, 'Term must be 1–1200 months'),
	startDate: required('Start date'),
	// dayOfMonthDue: server [Range(1, 31)].
	dayOfMonthDue: numericString('Day due').refine((v) => v >= 1 && v <= 31, 'Day must be 1–31'),
	monthlyPrincipalInterest: nonNegativeNumeric('Monthly P&I'),
	monthlyEscrow: nonNegativeNumeric('Monthly escrow'),
	escrowCoversTaxes: z.boolean(),
	escrowCoversInsurance: z.boolean(),
	status: z.string(),
	notes: optionalText,
});

// Recurring-expense template (insurance/tax/HOA entered once → materialized monthly).
export const recurringExpenseSchema = z.object({
	propertyId: idString,
	unitId: idString,
	category: z.string(),
	description: required('Description'),
	// amount: server [Range(0.01, …)] — must be > 0.
	amount: positiveNumeric('Amount'),
	frequency: z.string(),
	startDate: required('Start date'),
	notes: optionalText,
});

export const capitalAssetSchema = z.object({
	description: required('Description').max(500, 'Description must be 500 characters or fewer'),
	costBasis: positiveNumeric('Cost basis'),
	inServiceDate: required('In-service date'),
	method: z.enum(['StraightLine', 'Macrs']),
	recoveryYears: positiveNumeric('Recovery years').refine((v) => v <= 40, 'Recovery years cannot exceed 40'),
	convention: z.enum(['MidMonth', 'HalfYear']),
	accumulatedDepreciation: optionalNonNegative('Accumulated depreciation'),
});

export const propertyDispositionSchema = z.object({
	closedOnDate: required('Close date'),
	salePrice: positiveNumeric('Sale price'),
	sellingCosts: optionalNonNegative('Selling costs').transform((v) => v ?? 0),
	buyerName: optionalTextMax('Buyer', 200),
	memo: optionalTextMax('Memo', 1000),
});

export const evictionCaseSchema = z.object({
	status: z.enum(['Draft', 'NoticeServed', 'Filed', 'HearingScheduled', 'Judgment', 'MoveOut', 'Settled', 'Dismissed']),
	filedOnDate: optionalText,
	hearingDate: optionalText,
	courtName: optionalTextMax('Court', 200),
	caseNumber: optionalTextMax('Case number', 100),
	resolution: optionalTextMax('Resolution', 500),
	notes: optionalTextMax('Notes', 1000),
});

export const evictionCaseEventSchema = z.object({
	eventType: z.enum(['NoticeServed', 'Filed', 'HearingScheduled', 'Judgment', 'MoveOut', 'Settlement', 'Dismissal', 'PaymentPlan', 'Note']),
	eventDate: required('Event date'),
	notes: optionalTextMax('Notes', 1000),
});

export const capitalizeExpenseSchema = z.object({
	inServiceDate: required('In-service date'),
	method: z.enum(['StraightLine', 'Macrs']),
	recoveryYears: positiveNumeric('Recovery years').refine((v) => v <= 40, 'Recovery years cannot exceed 40'),
	convention: z.enum(['MidMonth', 'HalfYear']),
	description: optionalTextMax('Description', 500),
});

// Property depreciation basis (optional inline fields on the property edit form).
export const propertyBasisSchema = z
	.object({
		purchasePrice: optionalNonNegative('Purchase price'),
		landValue: optionalNonNegative('Land value'),
		inServiceDate: optionalText,
		manualAnnualDepreciation: optionalNonNegative('Manual depreciation'),
	})
	// Land is not depreciable, so the building basis is purchase − land. Land exceeding the purchase
	// price makes the basis negative (silently floored to 0 → $0 depreciation), so reject it up front.
	.refine((d) => d.purchasePrice == null || d.landValue == null || d.landValue <= d.purchasePrice, {
		path: ['landValue'],
		message: 'Land value cannot exceed the purchase price',
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
	addressLine1: optionalText,
	city: optionalText,
	state: optionalText,
	postalCode: optionalText,
	// email: server [EmailAddress] optional
	email: optionalEmail,
	phone: optionalText,
	website: optionalUrl,
	is1099Eligible: z.boolean(),
	w9OnFile: z.boolean(),
	preferred: z.boolean(),
});

/** Deduction to append to an existing canonical deposit account. */
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
