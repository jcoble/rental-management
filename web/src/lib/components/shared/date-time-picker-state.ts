export type DateTimeParts = {
	datePart: string;
	timePart: string;
};

export type DateTimePartState = DateTimeParts & {
	value: string;
};

export function splitDateTimeValue(iso: string): DateTimeParts {
	const [datePart = '', time = ''] = iso.split('T');
	return {
		datePart,
		timePart: time ? time.slice(0, 5) : ''
	};
}

export function combineDateTimeParts(datePart: string, timePart: string): string {
	if (!datePart || !timePart) return '';

	const time = timePart.length === 5 ? `${timePart}:00` : timePart;
	return `${datePart}T${time}`;
}

export function applyDateTimePartChange(current: DateTimeParts, change: Partial<DateTimeParts>): DateTimePartState {
	const datePart = change.datePart ?? current.datePart;
	const timePart = change.timePart ?? current.timePart;

	return {
		datePart,
		timePart,
		value: combineDateTimeParts(datePart, timePart)
	};
}
