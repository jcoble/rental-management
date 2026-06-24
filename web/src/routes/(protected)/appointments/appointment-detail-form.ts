import type { Appointment } from '$lib/types';

import { localWallClockToUtcIso, utcIsoToLocalWallClock } from './calendar-utils.ts';

export type AppointmentDetailForm = {
	title: string;
	type: string;
	status: string;
	scheduledStart: string;
	scheduledEnd: string;
	propertyId: string;
	tenantId: string;
	prospectName: string;
	prospectEmail: string;
	assignedTo: string;
};

type UtcToLocal = (utcIso: string | null | undefined) => string;
type LocalToUtc = (wallClock: string | null | undefined) => string;

export function createAppointmentDetailEditForm(
	appointment: Appointment,
	utcToLocal: UtcToLocal = utcIsoToLocalWallClock
): AppointmentDetailForm {
	return {
		title: appointment.title,
		type: appointment.type,
		status: appointment.status,
		scheduledStart: utcToLocal(appointment.scheduledStart),
		scheduledEnd: utcToLocal(appointment.scheduledEnd),
		propertyId: appointment.propertyId != null ? String(appointment.propertyId) : '',
		tenantId: appointment.tenantId != null ? String(appointment.tenantId) : '',
		prospectName: appointment.prospectName ?? '',
		prospectEmail: appointment.prospectEmail ?? '',
		assignedTo: appointment.assignedTo ?? '',
	};
}

export function buildAppointmentDetailSavePayload(
	parsedData: Record<string, unknown>,
	form: AppointmentDetailForm,
	portfolioId: number | null | undefined,
	localToUtc: LocalToUtc = localWallClockToUtcIso
): Record<string, unknown> {
	return {
		...parsedData,
		portfolioId,
		scheduledStart: localToUtc(form.scheduledStart),
		scheduledEnd: form.scheduledEnd ? localToUtc(form.scheduledEnd) : null,
	};
}
