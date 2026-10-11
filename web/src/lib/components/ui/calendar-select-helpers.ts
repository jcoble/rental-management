type NativeSelectChange = (
	event: Event & { currentTarget: EventTarget & HTMLSelectElement }
) => unknown;

export function calendarSelectDisabled(props: unknown): boolean {
	return Boolean((props as { disabled?: boolean }).disabled);
}

export function dispatchCalendarSelectChange(
	props: unknown,
	nextValue: string,
	externalChange?: NativeSelectChange | null
) {
	const event = {
		target: { value: nextValue },
		currentTarget: { value: nextValue }
	} as unknown as Parameters<NativeSelectChange>[0];
	const internalChange = (props as { onchange?: NativeSelectChange }).onchange;

	internalChange?.(event);
	externalChange?.(event);
}
