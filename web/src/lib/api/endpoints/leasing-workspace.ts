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
	openApplicationCount: number;
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
		api.get<LeasingPage<LeasingInboxItem>>(`/leasing/inbox/page${buildListQuery(params)}`)
};
