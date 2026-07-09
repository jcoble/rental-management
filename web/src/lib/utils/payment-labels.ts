/**
 * Friendly, landlord-facing labels for the Payment.PaymentType schema enum.
 *
 * The enum values are the wire/submitted values (must match the API contract);
 * these labels are what a non-technical landlord should ever SEE. Surfacing the
 * raw enum (e.g. "SecurityDeposit", "LateFee" — no space, schema casing) in the
 * money UI is the M-13 defect. Use {@link paymentTypeLabel} anywhere a payment
 * type is rendered; keep the enum as the value bound to the control/sent to the API.
 */

export const PAYMENT_TYPE_LABELS: Record<string, string> = {
	Rent: 'Rent',
	SecurityDeposit: 'Security deposit',
	LateFee: 'Late fee',
	Utility: 'Utility',
	ApplicationFee: 'Application fee',
	Other: 'Other'
};

/** Friendly label for a payment-type enum value; falls back to the raw value if unknown. */
export function paymentTypeLabel(type: string | null | undefined): string {
	if (!type) return '';
	return PAYMENT_TYPE_LABELS[type] ?? type;
}
