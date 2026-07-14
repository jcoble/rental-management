import { api, fetchApi } from '../client';
import { buildListQuery, type ListParams } from '../list-params';
import { idempotentMutation } from '../idempotency';

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
	hasScan: boolean;
	scanIsImage: boolean;
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
	/** Scope to one unit's applications (DB-side `?unitId=` filter; used by the Unit Command Center). */
	unitId?: number;
}

export interface ApplicationListResponse {
	items: ApplicationResponse[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface UpdateApplicationRequest {
	firstName?: string;
	lastName?: string;
	email?: string;
	phone?: string;
	dateOfBirth?: string;
	clearDateOfBirth?: boolean;
	currentAddress?: string;
	employer?: string;
	monthlyIncome?: number;
	clearMonthlyIncome?: boolean;
	desiredMoveInDate?: string;
	clearDesiredMoveInDate?: boolean;
	notes?: string;
	propertyId?: number;
	clearProperty?: boolean;
	unitId?: number;
	clearUnit?: boolean;
}

/** Outcome of a screening request. */
export type ScreeningStatus =
	| 'Created'
	| 'AwaitingProvider'
	| 'AwaitingApplicant'
	| 'InProgress'
	| 'Completed'
	| 'Failed'
	| 'Cancelled';

/** Landlord's recorded screening decision (string enum, matches the API). */
export type ScreeningRecommendation = 'Accept' | 'Conditional' | 'Decline';

/** Result of a gated tenant-screening run against an application. */
export interface ApplicantScreeningResponse {
	id: number;
	applicationId: number;
	mode: 'Integrated' | 'External';
	status: ScreeningStatus;
	providerDisplayName: string;
	providerReference: string | null;
	providerHostedUrl: string | null;
	consentConfirmed: boolean;
	invitedAtUtc: string | null;
	applicantSubmittedAtUtc: string | null;
	completedAtUtc: string | null;
	failedAtUtc: string | null;
	lastStatusAtUtc: string;
	decision: ScreeningRecommendation | null;
	decisionReason: string | null;
	consumerReportUsedForDecision: boolean;
	creditReportingAgencyName: string | null;
	creditReportingAgencyAddress: string | null;
	creditReportingAgencyPhone: string | null;
	hasCompleteCreditReportingAgencyContact: boolean;
	canGenerateAdverseAction: boolean;
	statusSummary: string;
	nextAction: string;
	isTerminal: boolean;
	canOpenProvider: boolean;
}

export interface ScreeningWorkspaceResponse {
	integratedProvider: {
		key: string | null;
		displayName: string | null;
		isConfigured: boolean;
		createsHostedInvitation: boolean;
		supportsStatusWebhooks: boolean;
		suppliesAdverseActionAgency: boolean;
		supportsApplicantPaidOrders: boolean;
		supportsLandlordPaidOrders: boolean;
	};
	screenings: ApplicantScreeningResponse[];
}

export interface TrackExternalScreeningRequest {
	operationKey: string;
	providerDisplayName: string;
	providerReference?: string | null;
	providerHostedUrl?: string | null;
	creditReportingAgencyName?: string | null;
	creditReportingAgencyAddress?: string | null;
	creditReportingAgencyPhone?: string | null;
	status: ScreeningStatus;
}

/** Body for recording an immutable fee collection in the application's pre-tenancy account. */
export interface RecordApplicationFeeRequest {
	amount: number;
	method?: string | null;
	currency: string;
	effectiveOn?: string | null;
}

export interface ApplicationFinanceMutationResponse {
	applicationId: number;
	accountId: number;
	entryId: number;
	relatedEntryId: number | null;
	entryType: 'FeeCollection' | 'Refund' | 'Adjustment';
	direction: 'Increase' | 'Decrease';
	amount: number;
	currency: string;
	effectiveOn: string;
	occurredAtUtc: string;
	accountCreated: boolean;
	replayed: boolean;
}

/** Request body for generating an FCRA adverse-action notice. */
export interface AdverseActionRequest {
	operationKey: string;
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
	return buildListQuery(params, { status: params?.status || undefined, unitId: params?.unitId });
}

/** Authed, portfolio-scoped landlord application endpoints (JWT). */
export const applications = {
	list: (params?: ApplicationListParams) =>
		api.get<ApplicationResponse[]>(`/applications${buildQuery(params)}`),
	listPage: (params?: ApplicationListParams) =>
		api.get<ApplicationListResponse>(`/applications/page${buildQuery(params)}`),
	get: (id: number) => api.get<ApplicationResponse>(`/applications/${id}`),
	update: (id: number, data: UpdateApplicationRequest) =>
		idempotentMutation(`applications:update:${id}:${JSON.stringify(data)}`, (operationKey) =>
			api.patch<ApplicationResponse>(`/applications/${id}`, data, {
				headers: { 'Idempotency-Key': operationKey }
			})
		),
	approve: (id: number) => idempotentMutation(`applications:approve:${id}`, (operationKey) =>
		api.post<ApproveApplicationResult>(`/applications/${id}/approve`, undefined, {
			headers: { 'Idempotency-Key': operationKey }
		})
	),
	decline: (id: number, reason?: string) =>
		idempotentMutation(`applications:decline:${id}:${reason ?? ''}`, (operationKey) =>
			api.post<ApplicationResponse>(`/applications/${id}/decline`, { reason: reason ?? null }, {
				headers: { 'Idempotency-Key': operationKey }
			})
		),
	withdraw: (id: number) => idempotentMutation(`applications:withdraw:${id}`, (operationKey) =>
		api.post<ApplicationResponse>(`/applications/${id}/withdraw`, undefined, {
			headers: { 'Idempotency-Key': operationKey }
		})
	),
	createLink: () => api.post<ApplicationLinkResult>('/applications/link'),
	startIntegratedScreening: (id: number, operationKey: string) =>
		api.post<ApplicantScreeningResponse>(`/applications/${id}/screening/integrated`, { operationKey }),
	trackExternalScreening: (id: number, body: TrackExternalScreeningRequest) =>
		api.post<ApplicantScreeningResponse>(`/applications/${id}/screening/external`, body),
	updateExternalScreening: (
		id: number,
		screeningId: number,
		body: {
			operationKey: string;
			status?: ScreeningStatus;
			providerReference?: string | null;
			providerHostedUrl?: string | null;
			creditReportingAgencyName?: string | null;
			creditReportingAgencyAddress?: string | null;
			creditReportingAgencyPhone?: string | null;
			occurredAtUtc?: string;
		}
	) =>
		api.patch<ApplicantScreeningResponse>(
			`/applications/${id}/screening/${screeningId}/external`,
			body
		),
	recordScreeningDecision: (
		id: number,
		screeningId: number,
		body: {
			operationKey: string;
			decision: ScreeningRecommendation;
			reason?: string | null;
			consumerReportUsed: boolean;
		}
	) =>
		api.post<ApplicantScreeningResponse>(
			`/applications/${id}/screening/${screeningId}/decision`,
			body
		),
	screening: (id: number) => api.get<ScreeningWorkspaceResponse>(`/applications/${id}/screening`),
	/** Record a fee collection idempotently in the application's pre-tenancy account. */
	recordFee: (id: number, operationKey: string, body: RecordApplicationFeeRequest) =>
		fetchApi<ApplicationFinanceMutationResponse>(`/applications/${id}/fee`, {
			method: 'POST',
			headers: { 'Content-Type': 'application/json', 'Idempotency-Key': operationKey },
			body: JSON.stringify(body),
		}),
	/** Generate an FCRA adverse-action notice (and optionally email the applicant). */
	adverseAction: (id: number, body: AdverseActionRequest) =>
		api.post<AdverseActionNoticeResponse>(`/applications/${id}/adverse-action`, body),
};
