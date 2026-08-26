/**
 * Who signs a lease first, decided for the landlord instead of asked.
 *
 * The tenants sign, then the landlord countersigns. That is what almost every
 * lease does, so "Create and send lease" fills it in and only shows the order
 * and role pickers when there is a real choice to make (more than one tenant).
 */

const TENANT_SIDE_ROLES = ['PrimaryTenant', 'CoTenant', 'Guarantor'];

/** True for the people renting the place (and anyone guaranteeing their rent). */
export function isTenantSideSigner(signerRole: string): boolean {
	return TENANT_SIDE_ROLES.includes(signerRole);
}

/** Tenants first, landlord side after, numbered 1..n. The input is left alone. */
export function defaultSignerOrder<T extends { signerRole: string }>(
	parties: readonly T[]
): Array<T & { signingOrder: number }> {
	const tenantSide = parties.filter((party) => isTenantSideSigner(party.signerRole));
	const landlordSide = parties.filter((party) => !isTenantSideSigner(party.signerRole));
	return [...tenantSide, ...landlordSide].map((party, index) => ({
		...party,
		signingOrder: index + 1
	}));
}

/** Only worth showing the order and role pickers when more than one tenant signs. */
export function needsSigningOrderControls(
	parties: readonly { signerRole: string }[]
): boolean {
	return parties.filter((party) => isTenantSideSigner(party.signerRole)).length > 1;
}
