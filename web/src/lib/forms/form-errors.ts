export type FormErrors = Record<string, string>;

export function clearFieldError<T extends FormErrors>(errors: T, field: string): T {
	if (!errors[field]) return errors;
	const next = { ...errors };
	delete next[field];
	return next as T;
}
