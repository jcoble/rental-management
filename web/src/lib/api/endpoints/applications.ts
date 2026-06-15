import { api } from '../client';
import type {
	ApplicationFormConfig,
	CustomFieldConfig,
	CustomFieldType,
	PublicApplicationProperty,
} from '../public-applications';

export type { ApplicationFormConfig, CustomFieldConfig, CustomFieldType };

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
	/** Raw JSON (array) of all employer/income rows; null when the single-income shape was used. */
	incomeSourcesJson: string | null;
	/** Raw JSON (object) of pet answers; null when the pets section was off/unanswered. */
	petsJson: string | null;
	/** Raw JSON (object) of custom-question answers keyed by field id; null when none. */
	customFieldAnswersJson: string | null;
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

/** Returned by GET /applications/form-config: the editable config + property/unit options for defaults. */
export interface FormConfigEditorResponse {
	config: ApplicationFormConfig;
	properties: PublicApplicationProperty[];
}

/** A custom field as the landlord submits it (id optional — the server generates one when missing). */
export interface CustomFieldInput {
	id?: string | null;
	label: string;
	type: CustomFieldType;
	required: boolean;
	options: string[];
}

/** Body for PUT /applications/form-config. */
export interface SaveFormConfigRequest {
	incomeSources: { enabled: boolean };
	pets: { enabled: boolean; askDeposit: boolean };
	customFields: CustomFieldInput[];
	defaults: {
		propertyId: number | null;
		unitId: number | null;
		desiredMoveInDate: string | null;
	};
	locked: string[];
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
	/** The portfolio's editable application-form config + property/unit options for defaults. */
	getFormConfig: () => api.get<FormConfigEditorResponse>('/applications/form-config'),
	/** Validate + save the landlord's application-form config. Returns the normalized saved config. */
	saveFormConfig: (body: SaveFormConfigRequest) =>
		api.put<ApplicationFormConfig>('/applications/form-config', body),
	/** Run a (gated) tenant screening. Requires FCRA consent on the application. */
	screen: (id: number) => api.post<ScreeningResultResponse>(`/applications/${id}/screen`),
	/** Prior screening results for an application, newest first. */
	screening: (id: number) => api.get<ScreeningResultResponse[]>(`/applications/${id}/screening`),
	/** Generate an FCRA adverse-action notice (and optionally email the applicant). */
	adverseAction: (id: number, body: AdverseActionRequest) =>
		api.post<AdverseActionNoticeResponse>(`/applications/${id}/adverse-action`, body),
};
