const STATUS_LABELS = new Map<string, string>([
	['InProgress', 'In progress'],
	['NeedsFollowUp', 'Needs follow-up'],
	['NoShow', 'No show'],
	['NoticeGiven', 'Notice given'],
	['OnHold', 'On hold'],
	['PendingSignature', 'Pending signature'],
	['WaitingParts', 'Waiting on parts'],
]);

function fallbackLabel(value: string): string {
	const words = value
		.replace(/[_-]+/g, ' ')
		.replace(/([a-z0-9])([A-Z])/g, '$1 $2')
		.trim();
	if (!words) return '';
	return words.charAt(0).toUpperCase() + words.slice(1).toLowerCase();
}

export function formatStatusLabel(value: string | undefined | null): string {
	if (!value) return '';
	return STATUS_LABELS.get(value) ?? fallbackLabel(value);
}

export function formatAuditChangeValue(field: string | undefined | null, value: string | undefined | null): string {
	if (value == null || value === '') return '—';
	const fieldName = (field ?? '').trim().toLowerCase();
	if (fieldName === 'status' || fieldName.endsWith(' status')) {
		return formatStatusLabel(value);
	}
	return value;
}
