import { api, downloadFile, fetchApi } from '../client';
import { buildListQuery, type ListParams } from '../list-params';
import { browser } from '$app/environment';

export type SecurityDepositStatus =
	| 'NotFunded'
	| 'Held'
	| 'PartiallyReturned'
	| 'Returned'
	| 'Withheld';

/** One canonical deposit projection, keyed by its owning continuous tenant account. */
export interface TenantAccountDeposit {
	securityDepositAccountId: number;
	tenantAccountId: number;
	leaseManagementId: number;
	originatingAgreementId: number;
	propertyId: number;
	propertyName: string;
	unitId: number;
	unitNumber: string;
	accountNumber: string;
	relationshipNumber: string;
	primaryTenantName?: string | null;
	currency: string;
	createdAtUtc: string;
	effectiveNowUtc: string;
	businessDate: string;
	totalReceived: number;
	totalDeductions: number;
	totalRefunded: number;
	totalTransferredIn: number;
	totalTransferredOut: number;
	netAdjustments: number;
	heldBalance: number;
	status: SecurityDepositStatus;
}

export interface TenantAccountDepositListParams extends ListParams {
	tenantAccountId?: number;
	propertyId?: number;
	status?: SecurityDepositStatus;
}

export interface TenantAccountDepositPage {
	items: TenantAccountDeposit[];
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
			headers: {
				'Content-Type': 'application/json',
				'Idempotency-Key': operationKey,
			},
			body: JSON.stringify(body),
		}
	);
}

export const securityDeposits = {
	listPage: (params: TenantAccountDepositListParams = {}) =>
		api.get<TenantAccountDepositPage>(
			`/tenant-accounts/deposits/page${buildListQuery(params, {
				tenantAccountId: params.tenantAccountId,
				propertyId: params.propertyId,
				status: params.status,
			})}`
		),

	get: (tenantAccountId: number) =>
		api.get<TenantAccountDeposit>(`/tenant-accounts/${tenantAccountId}/deposit`),

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
export async function downloadMoveOutStatement(tenantAccountId: number): Promise<void> {
	if (!browser) return;

	const blob = await downloadFile(
		`/tenant-accounts/${tenantAccountId}/deposit/move-out-statement`
	);
	const objectUrl = URL.createObjectURL(blob);
	const anchor = document.createElement('a');
	anchor.href = objectUrl;
	anchor.download = `move-out-statement-${tenantAccountId}.pdf`;
	document.body.appendChild(anchor);
	anchor.click();
	document.body.removeChild(anchor);
	URL.revokeObjectURL(objectUrl);
}
