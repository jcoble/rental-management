type QueryInvalidator = {
	invalidateQueries: (filters: { queryKey: readonly unknown[] }) => unknown;
};

export function appointmentsQueryKey(portfolioId: number | null | undefined) {
	return ['appointments', portfolioId] as const;
}

export function headerUpcomingAppointmentsQueryKey(portfolioId: number | null | undefined) {
	return ['header-upcoming-appointments', portfolioId] as const;
}

export function appointmentDetailQueryKey(appointmentId: number) {
	return ['appointment', appointmentId] as const;
}

export function invalidateAppointmentQueries(
	queryClient: QueryInvalidator,
	portfolioId: number | null | undefined,
	appointmentId?: number
) {
	queryClient.invalidateQueries({ queryKey: appointmentsQueryKey(portfolioId) });
	queryClient.invalidateQueries({ queryKey: headerUpcomingAppointmentsQueryKey(portfolioId) });
	if (appointmentId != null) {
		queryClient.invalidateQueries({ queryKey: appointmentDetailQueryKey(appointmentId) });
	}
}
