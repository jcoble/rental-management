import { api } from '../client';

/** Application lifecycle status (string enum, matches the API). */
export type ApplicationStatus =
	| 'Submitted'
	| 'UnderReview'
	| 'Approved'
	| 'Declined'
	| 'Withdrawn';

/** A rental application as returned by the authed landlord endpoints. */
export interface ApplicationResponse {
	id: number;
	propertyId: number | null;
	unitId: number | null;
	firstName: string;
	lastName: string;
	email: string;
	phone: string;
	dateOfBirth: string | null;
	currentAddress: string | null;
	employer: string | null;
	monthlyIncome: number | null;
	desiredMoveInDate: string | null;
	notes: string | null;
	consentGiven: boolean;
	consentAtUtc: string | null;
	status: ApplicationStatus;
	decisionReason: string | null;
	submittedAtUtc: string | null;
	reviewedAtUtc: string | null;
	approvedTenantId: number | null;
	testId: string | null;
}

export interface ApproveApplicationResult {
	applicationId: number;
	status: ApplicationStatus;
	tenantId: number;
}

export interface ApplicationLinkResult {
	token: string;
	/** Server-relative apply path, e.g. "/apply/{token}". */
	applyPath: string;
}

export interface ApplicationListParams {
	status?: ApplicationStatus | '';
	search?: string;
}

function buildQuery(params?: ApplicationListParams): string {
	const query = new URLSearchParams();
	if (params?.status) query.set('status', params.status);
	if (params?.search && params.search.trim().length > 0) query.set('search', params.search.trim());
	const qs = query.toString();
	return qs ? `?${qs}` : '';
}

/** Authed, portfolio-scoped landlord application endpoints (JWT). */
export const applications = {
	list: (params?: ApplicationListParams) =>
		api.get<ApplicationResponse[]>(`/applications${buildQuery(params)}`),
	get: (id: number) => api.get<ApplicationResponse>(`/applications/${id}`),
	approve: (id: number) => api.post<ApproveApplicationResult>(`/applications/${id}/approve`),
	decline: (id: number, reason?: string) =>
		api.post<ApplicationResponse>(`/applications/${id}/decline`, { reason: reason ?? null }),
	withdraw: (id: number) => api.post<ApplicationResponse>(`/applications/${id}/withdraw`),
	createLink: () => api.post<ApplicationLinkResult>('/applications/link'),
};
