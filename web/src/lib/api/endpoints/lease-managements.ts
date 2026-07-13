import type {
	LeaseAgreementSummary,
	LeaseManagementDetail,
	LeaseManagementLedger,
	LeaseManagementSummary,
	LeaseQuestionResponse
} from '$lib/types';
import { api, downloadFile } from '../client';

export interface LeaseManagementPageParams {
	skip?: number;
	take?: number;
	search?: string;
	sort?: string;
	propertyId?: number;
	unitId?: number;
	tenantId?: number;
	lifecycle?: string;
	hasReconciliationException?: boolean;
}

export interface LeaseManagementPage {
	items: LeaseManagementSummary[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface LeaseAgreementHistoryPage {
	items: LeaseAgreementSummary[];
	totalCount: number;
	skip: number;
	take: number;
}

function queryString<T extends object>(params: T): string {
	const query = new URLSearchParams();
	for (const [key, value] of Object.entries(params)) {
		if (value !== undefined && value !== null && value !== '') query.set(key, String(value));
	}
	const text = query.toString();
	return text ? `?${text}` : '';
}

export const leaseManagements = {
	listPage: (params: LeaseManagementPageParams = {}) =>
		api.get<LeaseManagementPage>(`/lease-managements/page${queryString(params)}`),
	get: (leaseManagementId: number) =>
		api.get<LeaseManagementDetail>(`/lease-managements/${leaseManagementId}`),
	ledger: (leaseManagementId: number, params: { skip?: number; take?: number } = {}) =>
		api.get<LeaseManagementLedger>(
			`/lease-managements/${leaseManagementId}/ledger${queryString(params)}`
		),
	ask: (leaseManagementId: number, question: string) =>
		api.post<LeaseQuestionResponse>(`/lease-managements/${leaseManagementId}/ask`, { question }),
	agreements: (
		leaseManagementId: number,
		params: {
			skip?: number;
			take?: number;
			status?: string;
			sort?: string;
		} = {}
	) =>
		api.get<LeaseAgreementHistoryPage>(
			`/lease-managements/${leaseManagementId}/agreements/page${queryString(params)}`
		),
	downloadArtifact: (leaseManagementId: number, leaseAgreementId: number, artifactId: number) =>
		downloadFile(
			`/lease-managements/${leaseManagementId}/agreements/${leaseAgreementId}/artifacts/${artifactId}`
		),
	downloadSourceScan: (leaseManagementId: number, leaseAgreementId: number) =>
		downloadFile(
			`/lease-managements/${leaseManagementId}/agreements/${leaseAgreementId}/source-scan`
		)
};
