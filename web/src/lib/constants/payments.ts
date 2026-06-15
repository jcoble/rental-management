/**
 * Canonical payment-method list, shared by every web surface that offers a payment method
 * (the Mark Paid modals on Money + Who's-behind, etc.). `method` is stored as free text and the
 * payment search filters on it (ILIKE), so web and mobile MUST offer the SAME string values or
 * reporting/search fragments across clients (a card payment recorded as "Card" on one platform and
 * "Credit card" on the other never reconcile).
 *
 * FROZEN — keep these strings byte-for-byte in sync with the mobile list
 * (`mobile/lib/features/payments/record_payment_sheet.dart`). Order is intentional: the everyday
 * methods first. Add new methods to BOTH platforms in the same change.
 */
export const PAYMENT_METHODS = [
	'Cash',
	'Check',
	'Card',
	'Bank transfer',
	'Zelle',
	'Venmo',
	'Money order',
	'Online portal',
	'Other'
] as const;

export type PaymentMethod = (typeof PAYMENT_METHODS)[number];
