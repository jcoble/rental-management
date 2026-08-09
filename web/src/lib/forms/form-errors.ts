export type FormErrors = Record<string, string>;

export function clearFieldError<T extends FormErrors>(errors: T, field: string): T {
	if (!errors[field]) return errors;
	const next = { ...errors };
	delete next[field];
	return next as T;
}

/** Map ASP.NET ValidationProblemDetails keys to the camelCase names used by form state. */
export function validationErrorsToFormErrors(
	validationErrors?: Record<string, string[] | string | undefined> | null
): FormErrors {
	if (!validationErrors) return {};
	const formErrors: FormErrors = {};
	for (const [rawKey, rawMessages] of Object.entries(validationErrors)) {
		const key = rawKey.replace(/^\$\./, '').split('.').pop() ?? rawKey;
		const messages = Array.isArray(rawMessages) ? rawMessages : [rawMessages];
		const message = messages.find((candidate): candidate is string => Boolean(candidate));
		if (!message || !key) continue;
		const field = `${key.charAt(0).toLowerCase()}${key.slice(1)}`;
		if (!formErrors[field]) formErrors[field] = message;
	}
	return formErrors;
}

export function formErrorsFromApiError(error: unknown): FormErrors {
	if (!error || typeof error !== 'object' || !('validationErrors' in error)) return {};
	const validationErrors = (error as { validationErrors?: Record<string, string[] | string | undefined> })
		.validationErrors;
	return validationErrorsToFormErrors(validationErrors);
}
