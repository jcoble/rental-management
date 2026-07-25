import type {
	LeaseAddendumFinancialEffectType,
	LeaseAddendumPurpose,
	LeaseLegalSignerRole,
	LegalDocumentIssuancePreparation
} from './lease-managements';
import type { LegalDocumentArtifactSummary } from '$lib/types';
import { api, downloadFile, fetchApi } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export interface LeaseAddendumHistoryParams extends ListParams {
	status?: string;
}

export interface LeaseAddendumHistoryItem {
	leaseManagementId: number;
	leaseAddendumId: number;
	publicId: string;
	seriesPublicId: string;
	baseAgreementId: number;
	versionNumber: number;
	addendumNumber: string;
	purpose: LeaseAddendumPurpose;
	replacesAddendumId: number | null;
	effectiveFromOn: string;
	effectiveThroughOn: string | null;
	supersededEffectiveOn: string | null;
	addendumStatus: string;
	financialEffectCount: number;
	recurringRentDelta: number;
	signerCount: number;
	issuedArtifact: LegalDocumentArtifactSummary | null;
	executedArtifact: LegalDocumentArtifactSummary | null;
	issuedAtUtc: string | null;
	fullyExecutedAtUtc: string | null;
	voidedAtUtc: string | null;
	createdAtUtc: string;
	updatedAtUtc: string;
}

export interface LeaseAddendumHistoryPage {
	items: LeaseAddendumHistoryItem[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface LeaseAddendumDraftSigner {
	leaseAddendumSignerId: number;
	leaseManagementPartyId: number | null;
	tenantId: number | null;
	signerRole: LeaseLegalSignerRole;
	nameSnapshot: string;
	emailSnapshot: string;
	signingOrder: number;
	isRequired: boolean;
}

export interface LeaseAddendumDraftFinancialEffect {
	leaseAddendumFinancialEffectId: number;
	effectType: LeaseAddendumFinancialEffectType;
	amount: number;
	currency: string;
	chargeCode: string;
	effectiveFromOn: string | null;
	effectiveThroughOn: string | null;
	dueOn: string | null;
	description: string;
}

export interface LeaseAddendumDraftDetail {
	leaseManagementId: number;
	leaseAddendumId: number;
	publicId: string;
	seriesPublicId: string;
	baseAgreementId: number;
	baseAgreementPublicId: string;
	baseAgreementNumber: string;
	baseAgreementCurrency: string;
	versionNumber: number;
	draftRevision: number;
	addendumNumber: string;
	purpose: LeaseAddendumPurpose;
	sourceAddendumId: number | null;
	effectiveFromOn: string;
	effectiveThroughOn: string | null;
	termsSchemaVersion: number;
	termsPayload: Record<string, unknown>;
	documentSourceVersionId: number;
	documentTemplateId: number | null;
	documentTemplateVersion: number | null;
	signers: LeaseAddendumDraftSigner[];
	financialEffects: LeaseAddendumDraftFinancialEffect[];
	createdAtUtc: string;
	updatedAtUtc: string;
}

export interface LeaseAddendumSignerRequest {
	leaseManagementPartyId: number | null;
	tenantId: number | null;
	signerRole: LeaseLegalSignerRole;
	nameSnapshot: string;
	emailSnapshot: string;
	signingOrder: number;
	isRequired: boolean;
}

export interface LeaseAddendumFinancialEffectRequest {
	effectType: LeaseAddendumFinancialEffectType;
	amount: number;
	currency: string;
	chargeCode: string;
	effectiveFromOn: string | null;
	effectiveThroughOn: string | null;
	dueOn: string | null;
	description: string;
}

export interface CreateLeaseAddendumDraftRequest {
	baseAgreementId: number;
	addendumNumber: string;
	purpose: LeaseAddendumPurpose;
	effectiveFromOn: string;
	effectiveThroughOn: string | null;
	termsSchemaVersion: number;
	termsPayload: Record<string, unknown>;
	documentTemplateId: number;
	signers: LeaseAddendumSignerRequest[];
	financialEffects: LeaseAddendumFinancialEffectRequest[];
}

export interface EditLeaseAddendumDraftRequest extends CreateLeaseAddendumDraftRequest {
	draftRevision: number;
}

export interface LeaseAddendumDraftMutationResponse {
	leaseManagementId: number;
	leaseAddendumId: number;
	seriesPublicId: string;
	versionNumber: number;
	draftRevision: number;
	sourceAddendumId: number | null;
	leaseAddendumSignerIds: number[];
	financialEffectIds: number[];
	replayed: boolean;
}

export interface IssueLeaseAddendumResponse {
	signatureRequestPublicId: string;
	leaseManagementId: number;
	leaseAddendumId: number;
	signatureRequestId: number;
	issuedArtifactId: number;
	replayed: boolean;
}

function historyQuery(params: LeaseAddendumHistoryParams = {}) {
	return buildListQuery(params, { status: params.status });
}

function idempotentJson<T>(path: string, method: 'POST' | 'PATCH', request: unknown, operationKey: string) {
	return fetchApi<T>(path, {
		method,
		headers: { 'Idempotency-Key': operationKey },
		body: JSON.stringify(request)
	});
}

export const leaseAddendums = {
	listPage: (leaseManagementId: number, params: LeaseAddendumHistoryParams = {}) =>
		api.get<LeaseAddendumHistoryPage>(
			`/lease-managements/${leaseManagementId}/addenda/page${historyQuery(params)}`
		),
	getDraft: (leaseManagementId: number, leaseAddendumId: number) =>
		api.get<LeaseAddendumDraftDetail>(
			`/lease-managements/${leaseManagementId}/addenda/${leaseAddendumId}/draft`
		),
	createDraft: (
		leaseManagementId: number,
		request: CreateLeaseAddendumDraftRequest,
		operationKey: string
	) =>
		idempotentJson<LeaseAddendumDraftMutationResponse>(
			`/lease-managements/${leaseManagementId}/addenda`,
			'POST',
			request,
			operationKey
		),
	editDraft: (
		leaseManagementId: number,
		leaseAddendumId: number,
		request: EditLeaseAddendumDraftRequest,
		operationKey: string
	) =>
		idempotentJson<LeaseAddendumDraftMutationResponse>(
			`/lease-managements/${leaseManagementId}/addenda/${leaseAddendumId}/draft`,
			'PATCH',
			request,
			operationKey
		),
	correctDraft: (
		leaseManagementId: number,
		sourceAddendumId: number,
		supersessionEffectiveOn: string,
		operationKey: string
	) =>
		idempotentJson<LeaseAddendumDraftMutationResponse>(
			`/lease-managements/${leaseManagementId}/addenda/${sourceAddendumId}/correct`,
			'POST',
			{ supersessionEffectiveOn },
			operationKey
		),
	prepareIssuance: (
		leaseManagementId: number,
		leaseAddendumId: number,
		draftRevision: number,
		operationKey: string
	) =>
		idempotentJson<LegalDocumentIssuancePreparation>(
			`/lease-managements/${leaseManagementId}/addenda/${leaseAddendumId}/issuance-preparations`,
			'POST',
			{ draftRevision },
			operationKey
		),
	issue: (
		leaseManagementId: number,
		leaseAddendumId: number,
		request: LegalDocumentIssuancePreparation & { subject: string },
		operationKey: string
	) =>
		idempotentJson<IssueLeaseAddendumResponse>(
			`/lease-managements/${leaseManagementId}/addenda/${leaseAddendumId}/issue`,
			'POST',
			request,
			operationKey
		),
	downloadArtifact: (
		leaseManagementId: number,
		leaseAddendumId: number,
		artifactId: number
	) =>
		downloadFile(
			`/lease-managements/${leaseManagementId}/addenda/${leaseAddendumId}/artifacts/${artifactId}`
		)
};
