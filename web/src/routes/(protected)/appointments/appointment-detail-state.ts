import { formatStatusLabel } from '../../../lib/utils/status-labels.ts';

export const APPT_STATUSES = ['Scheduled', 'Confirmed', 'Completed', 'Cancelled', 'NoShow'] as const;

type AppointmentStatus = (typeof APPT_STATUSES)[number] | string;

export function appointmentStatusOptions() {
	return APPT_STATUSES.map((value) => ({ value, label: formatStatusLabel(value) }));
}

export function appointmentDetailStatusLabel(status: AppointmentStatus | undefined | null): string {
	return formatStatusLabel(status);
}

export function appointmentDetailStatusActions(status: AppointmentStatus) {
	const active = status === 'Scheduled' || status === 'Confirmed';
	return {
		canConfirm: status === 'Scheduled',
		canComplete: active,
		canCancel: active,
		canNoShow: active,
	};
}
