import type { SecurityDepositAccount } from '$lib/types';
import { api, fetchApi, refreshToken } from '../client';
import { buildListQuery, type ListParams } from '../list-params';
import { CLIENT_API_BASE_URL } from '$lib/config';
import { getAuthState, isTokenExpired } from '$lib/stores/auth.svelte';
import { browser } from '$app/environment';

export interface SecurityDepositListParams extends ListParams {
	leaseManagementId?: number;
}

export interface SecurityDepositListResponse {
	items: SecurityDepositAccount[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface FundSecurityDepositRequest {
	securityDepositAccountId: number;
	amount: number;
	effectiveOn: string;
	description: string;
	paymentMethodSummary: string;
	externalReference?: string;
	sourceStoredFileId?: number;
}

export interface DeductSecurityDepositRequest {
	securityDepositAccountId: number;
	amount: number;
	effectiveOn: string;
	reason: string;
	notes?: string;
	sourceStoredFileId?: number;
}

export interface RefundSecurityDepositRequest {
	securityDepositAccountId: number;
	amount?: number;
	effectiveOn: string;
	description: string;
	externalReference?: string;
}

export interface SecurityDepositMutationResult {
	found: boolean;
	applied: boolean;
	tenantAccountId: number;
	securityDepositAccountId: number;
	securityDepositEntryId: number;
	tenantLedgerEntryId: number;
	amount: number;
	error?: string;
}

export interface SecurityDepositMutationResponse {
	value: SecurityDepositMutationResult;
	replayed: boolean;
}

function postDepositMutation(
	tenantAccountId: number,
	route: 'fund' | 'deductions' | 'refunds',
	operationKey: string,
	body: FundSecurityDepositRequest | DeductSecurityDepositRequest | RefundSecurityDepositRequest
) {
	return fetchApi<SecurityDepositMutationResponse>(
		`/tenant-accounts/${tenantAccountId}/deposit/${route}`,
		{
			method: 'POST',
			headers: { 'Content-Type': 'application/json', 'Idempotency-Key': operationKey },
			body: JSON.stringify(body),
		}
	);
}

export const securityDeposits = {
	/** Canonical account projections, optionally scoped to one continuous lease relationship. */
	list: (leaseManagementId?: number) =>
		api.get<SecurityDepositAccount[]>(
			leaseManagementId != null
				? `/security-deposits?leaseManagementId=${leaseManagementId}`
				: '/security-deposits'
		),

	listPage: (params?: SecurityDepositListParams) =>
		api.get<SecurityDepositListResponse>(
			`/security-deposits/page${buildListQuery(params, { leaseManagementId: params?.leaseManagementId })}`
		),

	get: (id: number) => api.get<SecurityDepositAccount>(`/security-deposits/${id}`),

	fund: (tenantAccountId: number, operationKey: string, body: FundSecurityDepositRequest) =>
		postDepositMutation(tenantAccountId, 'fund', operationKey, body),

	addDeduction: (
		tenantAccountId: number,
		operationKey: string,
		body: DeductSecurityDepositRequest
	) => postDepositMutation(tenantAccountId, 'deductions', operationKey, body),

	refund: (tenantAccountId: number, operationKey: string, body: RefundSecurityDepositRequest) =>
		postDepositMutation(tenantAccountId, 'refunds', operationKey, body),
};

/** Download the canonical account's move-out statement with the bearer token attached. */
export async function downloadMoveOutStatement(id: number): Promise<void> {
	if (!browser) return;

	if (isTokenExpired(120)) {
		try {
			await refreshToken();
		} catch {
			// Proceed; bearer may still be usable.
		}
	}

	const { accessToken } = getAuthState();
	const response = await fetch(`${CLIENT_API_BASE_URL}/security-deposits/${id}/move-out-statement`, {
		credentials: 'include',
		headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {},
	});

	if (!response.ok) throw new Error(`Move-out statement download failed (${response.status})`);

	const blob = await response.blob();
	const objectUrl = URL.createObjectURL(blob);
	const anchor = document.createElement('a');
	anchor.href = objectUrl;
	anchor.download = `move-out-statement-${id}.pdf`;
	document.body.appendChild(anchor);
	anchor.click();
	document.body.removeChild(anchor);
	URL.revokeObjectURL(objectUrl);
}
