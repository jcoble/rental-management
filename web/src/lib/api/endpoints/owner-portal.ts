import type { OwnerStatementReport, OwnerStatementSummary } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export interface OwnerPortalOverview {
	currentYear: number;
	propertyCount: number;
	unitCount: number;
	distributedThisYear: number;
	pendingApprovalCount: number;
	unreadMessageCount: number;
}

export interface OwnerPortalProperty {
	id: number;
	name: string;
	propertyType: string;
	status: string;
	addressLine1: string;
	addressLine2?: string | null;
	city: string;
	state: string;
	postalCode: string;
	unitCount: number;
}

export interface OwnerPortalPropertyPage {
	items: OwnerPortalProperty[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface OwnerPortalItem {
	id: number;
	title: string;
	message: string;
	severity: string;
	isRead: boolean;
	createdAt: string;
}

export interface OwnerPortalItemPage {
	items: OwnerPortalItem[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface OwnerPortalDistribution {
	id: number;
	ownerEntityId: number;
	ownerName: string;
	propertyId?: number | null;
	propertyName?: string | null;
	date: string;
	amount: number;
	method: string;
	memo?: string | null;
}

export interface OwnerPortalDistributionPage {
	items: OwnerPortalDistribution[];
	totalCount: number;
	skip: number;
	take: number;
}

export const ownerPortal = {
	overview: () => api.get<OwnerPortalOverview>('/owner/overview'),
	propertiesPage: (params?: ListParams) =>
		api.get<OwnerPortalPropertyPage>(`/owner/properties/page${buildListQuery(params)}`),
	statements: (year: number) =>
		api.get<OwnerStatementSummary[]>(`/owner/statements?year=${year}`),
	statement: (ownerEntityId: number, year: number) =>
		api.get<OwnerStatementReport>(`/owner/statements/${ownerEntityId}?year=${year}`),
	distributionsPage: (params?: ListParams) =>
		api.get<OwnerPortalDistributionPage>(`/owner/distributions/page${buildListQuery(params)}`),
	approvalsPage: (params?: ListParams) =>
		api.get<OwnerPortalItemPage>(`/owner/approvals/page${buildListQuery(params)}`),
	messagesPage: (params?: ListParams) =>
		api.get<OwnerPortalItemPage>(`/owner/messages/page${buildListQuery(params)}`)
};
