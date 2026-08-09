export type InputMask =
	| 'currency'
	| 'decimal'
	| 'percentage'
	| 'integer'
	| 'phone'
	| 'zip'
	| 'cardLast4';

type DecimalOptions = {
	maxDecimals?: number;
};

type MaskOptions = {
	maxLength?: number;
	maxDecimals?: number;
};

/** Apply the same maxlength boundary that the rendered Input exposes to the browser. */
export function limitInputLength(value: string, maxLength?: number): string {
	if (typeof maxLength !== 'number' || !Number.isFinite(maxLength)) return value;
	return value.slice(0, Math.max(0, Math.floor(maxLength)));
}

export function maskInputValue(value: string, mask: InputMask, options: MaskOptions = {}): string {
	const decimalOptions = typeof options.maxDecimals === 'number' ? { maxDecimals: options.maxDecimals } : {};

	switch (mask) {
		case 'currency':
			return maskCurrencyInput(value);
		case 'decimal':
			return maskDecimalInput(value, decimalOptions);
		case 'percentage':
			return maskPercentageInput(value, decimalOptions);
		case 'integer':
			return maskIntegerInput(value, options.maxLength);
		case 'phone':
			return maskPhoneInput(value);
		case 'zip':
			return maskZipInput(value);
		case 'cardLast4':
			return maskCardLast4Input(value);
	}
}

export function maskDecimalInput(value: string, options: DecimalOptions = {}): string {
	const maxDecimals = options.maxDecimals;
	let cleaned = value.replace(/[^0-9.]/g, '');
	const firstDot = cleaned.indexOf('.');

	if (firstDot !== -1) {
		const whole = cleaned.slice(0, firstDot);
		let decimals = cleaned.slice(firstDot + 1).replace(/\./g, '');
		if (typeof maxDecimals === 'number') decimals = decimals.slice(0, maxDecimals);
		cleaned = `${whole}.${decimals}`;
	}

	if (cleaned.startsWith('.')) return `0${cleaned}`;
	return cleaned;
}

export function maskCurrencyInput(value: string): string {
	return maskDecimalInput(value, { maxDecimals: 2 });
}

export function maskPercentageInput(value: string, options: DecimalOptions = {}): string {
	return maskDecimalInput(value, { maxDecimals: options.maxDecimals ?? 4 });
}

export function maskIntegerInput(value: string, maxLength?: number): string {
	const digits = value.replace(/\D/g, '');
	return typeof maxLength === 'number' ? digits.slice(0, maxLength) : digits;
}

export function maskCardLast4Input(value: string): string {
	return maskIntegerInput(value, 4);
}

export function maskZipInput(value: string): string {
	const digits = maskIntegerInput(value, 9);
	if (digits.length <= 5) return digits;
	return `${digits.slice(0, 5)}-${digits.slice(5)}`;
}

export function maskPhoneInput(value: string): string {
	const digits = value.replace(/\D/g, '');
	if (!digits) return '';

	const hasCountryCode = digits.length > 10 && digits.startsWith('1');
	const country = hasCountryCode ? '1' : '';
	const local = hasCountryCode ? digits.slice(1, 11) : digits.slice(0, 10);
	const extension = hasCountryCode ? digits.slice(11) : digits.slice(10);

	const parts: string[] = [];
	if (country) parts.push(country);
	if (local.length <= 3) {
		if (local) parts.push(local);
	} else if (local.length <= 6) {
		parts.push(local.slice(0, 3), local.slice(3));
	} else {
		parts.push(local.slice(0, 3), local.slice(3, 6), local.slice(6));
	}

	const formatted = parts.join('-');
	return extension ? `${formatted} x${extension.slice(0, 6)}` : formatted;
}
