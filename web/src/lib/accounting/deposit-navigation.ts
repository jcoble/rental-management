export interface DepositNavigationIdentity {
	tenantAccountId: number;
	securityDepositAccountId: number;
}

/** The deposit detail route is tenant-account scoped; the security-deposit account is a document key. */
export function depositDetailHref(deposit: DepositNavigationIdentity): string {
	return `/deposits/${deposit.tenantAccountId}`;
}
