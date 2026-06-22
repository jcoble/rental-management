import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

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
	propertyName: string | null;
	unitNumber: string | null;
	firstName: string;
	lastName: string;
	email: string;
	phone: string;
	dateOfBirth: string | null;
	currentAddressLine1: string | null;
	currentAddressLine2: string | null;
	currentCity: string | null;
	currentState: string | null;
	currentPostalCode: string | null;
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

export interface ApplicationListParams extends ListParams {
	status?: ApplicationStatus | '';
}

export interface ApplicationListResponse {
	items: ApplicationResponse[];
	totalCount: number;
	skip: number;
	take: number;
}

/** Outcome of a screening request. */
export type ScreeningStatus = 'Requested' | 'Completed' | 'Failed';

/** Screening recommendation (string enum, matches the API). */
export type ScreeningRecommendation = 'Accept' | 'Conditional' | 'Decline';

/** Result of a gated tenant-screening run against an application. */
export interface ScreeningResultResponse {
	id: number;
	applicationId: number;
	status: ScreeningStatus;
	creditScoreBand: string | null;
	hasCriminalRecord: boolean;
	hasEvictionRecord: boolean;
	recommendation: ScreeningRecommendation | null;
	providerReference: string | null;
	requestedAtUtc: string | null;
	completedAtUtc: string | null;
}

/** Request body for generating an FCRA adverse-action notice. */
export interface AdverseActionRequest {
	reason?: string;
	sendToApplicant: boolean;
}

/** A generated FCRA adverse-action notice. */
export interface AdverseActionNoticeResponse {
	id: number;
	applicationId: number;
	reason: string | null;
	creditReportingAgency: string | null;
	generatedAtUtc: string | null;
	storedFileId: number;
	sentAtUtc: string | null;
}

function buildQuery(params?: ApplicationListParams): string {
	return buildListQuery(params, { status: params?.status || undefined });
}

/** Authed, portfolio-scoped landlord application endpoints (JWT). */
export const applications = {
	list: (params?: ApplicationListParams) =>
		api.get<ApplicationResponse[]>(`/applications${buildQuery(params)}`),
	listPage: (params?: ApplicationListParams) =>
		api.get<ApplicationListResponse>(`/applications/page${buildQuery(params)}`),
	get: (id: number) => api.get<ApplicationResponse>(`/applications/${id}`),
	approve: (id: number) => api.post<ApproveApplicationResult>(`/applications/${id}/approve`),
	decline: (id: number, reason?: string) =>
		api.post<ApplicationResponse>(`/applications/${id}/decline`, { reason: reason ?? null }),
	withdraw: (id: number) => api.post<ApplicationResponse>(`/applications/${id}/withdraw`),
	createLink: () => api.post<ApplicationLinkResult>('/applications/link'),
	/** Run a (gated) tenant screening. Requires FCRA consent on the application. */
	screen: (id: number) => api.post<ScreeningResultResponse>(`/applications/${id}/screen`),
	/** Prior screening results for an application, newest first. */
	screening: (id: number) => api.get<ScreeningResultResponse[]>(`/applications/${id}/screening`),
	/** Generate an FCRA adverse-action notice (and optionally email the applicant). */
	adverseAction: (id: number, body: AdverseActionRequest) =>
		api.post<AdverseActionNoticeResponse>(`/applications/${id}/adverse-action`, body),
};
