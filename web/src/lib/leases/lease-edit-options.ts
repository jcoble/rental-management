export type SelectOption = {
	value: string;
	label: string;
};

export function ensureSelectedOption<T extends SelectOption>(
	options: T[],
	selectedValue: string | number | null | undefined,
	selectedLabel: string | null | undefined
): T[] {
	const value = selectedValue == null ? '' : String(selectedValue);
	if (!value || options.some((option) => option.value === value)) {
		return options;
	}

	const label = String(selectedLabel ?? '').trim();
	if (!label) {
		return options;
	}

	const selected = { value, label } as T;
	if (options[0]?.value === '') {
		return [options[0], selected, ...options.slice(1)];
	}

	return [selected, ...options];
}
