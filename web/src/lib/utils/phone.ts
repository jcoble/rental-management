/**
 * Formats a raw phone string to (555) 555-5555 as the user types.
 * Strips non-digits, limits to 10 digits, then applies the mask.
 */
export function formatPhoneInput(raw: string): string {
	const digits = raw.replace(/\D/g, '').slice(0, 10);
	if (digits.length <= 3) return digits.length ? `(${digits}` : '';
	if (digits.length <= 6) return `(${digits.slice(0, 3)}) ${digits.slice(3)}`;
	return `(${digits.slice(0, 3)}) ${digits.slice(3, 6)}-${digits.slice(6)}`;
}
