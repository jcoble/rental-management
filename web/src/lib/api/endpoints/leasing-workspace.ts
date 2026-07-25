import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export interface LeasingToday {
	applicationsToReview: number;
	listingsNeedingAttention: number;
	showingsToday: number;
	upcomingMoveIns: number;
	unreadConversations: number;
}

export interface LeasingPipelineItem {
	kind: 'Application' | 'Listing' | 'MoveIn';
	recordId: number;
	propertyId?: number | null;
	unitId?: number | null;
	title: string;
	propertyName?: string | null;
	unitNumber?: string | null;
	stage: string;
	nextActionAtUtc?: string | null;
	updatedAtUtc: string;
}

export interface LeasingRental {
	propertyId: number;
	unitId: number;
	propertyName: string;
	unitNumber: string;
	address: string;
	listingId?: number | null;
	listingStatus?: string | null;
	listingHeadline?: string | null;
	askingRent?: number | null;
	availableOn?: string | null;
	canViewApplications: boolean;
	openApplicationCount: number;
	canViewShowings: boolean;
	nextShowingAtUtc?: string | null;
}

export interface LeasingCalendarItem {
	id: number;
	propertyId?: number | null;
	unitId?: number | null;
	rentalApplicationId?: number | null;
	title: string;
	prospectName?: string | null;
	propertyName?: string | null;
	unitNumber?: string | null;
	type: string;
	status: string;
	scheduledStart: string;
	scheduledEnd?: string | null;
}

export interface LeasingInboxItem {
	id: number;
	tenantName: string;
	subject: string;
	propertyName?: string | null;
	lastMessagePreview?: string | null;
	lastMessageAt: string;
	unreadCount: number;
}

export interface LeasingRentalDetail extends LeasingRental {
	listingDescription?: string | null;
	securityDeposit?: number | null;
	leaseTerms?: string | null;
	petPolicy?: string | null;
}

export interface LeasingApplicationDetail {
	id: number;
	propertyId?: number | null;
	unitId?: number | null;
	applicantName: string;
	email?: string | null;
	phone?: string | null;
	propertyName?: string | null;
	unitNumber?: string | null;
	status: string;
	monthlyIncome?: number | null;
	desiredMoveInDate?: string | null;
	notes?: string | null;
	consentGiven: boolean;
	submittedAtUtc: string;
}

export interface LeasingAppointmentDetail extends LeasingCalendarItem {
	prospectEmail?: string | null;
	assignedTo?: string | null;
	notes?: string | null;
}

export interface LeasingConversationMessage {
	id: number;
	senderRole: string;
	body: string;
	createdAt: string;
}

export interface LeasingConversationDetail {
	id: number;
	tenantName: string;
	subject: string;
	propertyName?: string | null;
	messages: LeasingConversationMessage[];
}

export interface LeasingMoveInDetail {
	id: number;
	propertyId: number;
	unitId: number;
	relationshipNumber: string;
	tenantName: string;
	propertyName: string;
	unitNumber: string;
	plannedPossessionAtUtc?: string | null;
	agreementFullyExecuted: boolean;
	possessionGiven: boolean;
}

export interface LeasingPage<T> {
	items: T[];
	totalCount: number;
	skip: number;
	take: number;
}

export const leasingWorkspace = {
	today: () => api.get<LeasingToday>('/leasing/today'),
	pipelinePage: (params?: ListParams) =>
		api.get<LeasingPage<LeasingPipelineItem>>(`/leasing/pipeline/page${buildListQuery(params)}`),
	rentalsPage: (params?: ListParams) =>
		api.get<LeasingPage<LeasingRental>>(`/leasing/rentals/page${buildListQuery(params)}`),
	calendarPage: (params?: ListParams) =>
		api.get<LeasingPage<LeasingCalendarItem>>(`/leasing/calendar/page${buildListQuery(params)}`),
	inboxPage: (params?: ListParams) =>
		api.get<LeasingPage<LeasingInboxItem>>(`/leasing/inbox/page${buildListQuery(params)}`),
	rental: (unitId: number) => api.get<LeasingRentalDetail>(`/leasing/rentals/${unitId}`),
	application: (id: number) => api.get<LeasingApplicationDetail>(`/leasing/applications/${id}`),
	appointment: (id: number) => api.get<LeasingAppointmentDetail>(`/leasing/appointments/${id}`),
	conversation: (id: number) => api.get<LeasingConversationDetail>(`/leasing/conversations/${id}`),
	reply: (id: number, body: string) => api.post<void>(`/leasing/conversations/${id}/messages`, {
		operationKey: crypto.randomUUID(),
		body,
		channels: ['Portal']
	}),
	moveIn: (id: number) => api.get<LeasingMoveInDetail>(`/leasing/move-ins/${id}`)
};
