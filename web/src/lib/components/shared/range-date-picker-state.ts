function parseIsoDate(iso: string): Date {
	return new Date(`${iso}T00:00:00Z`);
}

function isoDate(date: Date): string {
	return date.toISOString().slice(0, 10);
}

export function calendarPlaceholderForStart(start: string): string | undefined {
	if (!start) return undefined;
	const date = parseIsoDate(start);
	return Number.isNaN(date.getTime()) ? undefined : isoDate(new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), 1)));
}

export function servicePeriodPreset(offset: number, todayIso: string): { label: 'This month' | 'Last month'; start: string; end: string } {
	const today = parseIsoDate(todayIso);
	const year = today.getUTCFullYear();
	const month = today.getUTCMonth() + offset;
	const start = new Date(Date.UTC(year, month, 1));
	const end = new Date(Date.UTC(year, month + 1, 0));
	return {
		label: offset === 0 ? 'This month' : 'Last month',
		start: isoDate(start),
		end: isoDate(end),
	};
}

export function servicePeriodPresetState(start: string, end: string, todayIso: string): 'This month' | 'Last month' | 'Custom' {
	const thisMonth = servicePeriodPreset(0, todayIso);
	if (start === thisMonth.start && end === thisMonth.end) return 'This month';
	const lastMonth = servicePeriodPreset(-1, todayIso);
	if (start === lastMonth.start && end === lastMonth.end) return 'Last month';
	return 'Custom';
}
