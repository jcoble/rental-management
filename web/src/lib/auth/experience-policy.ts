import type { AccessEnvelope, WorkspaceExperience } from '$lib/types/user';

export const CAPABILITY = {
	rentalsRead: 'rentals.read',
	rentalsManage: 'rentals.manage',
	workRead: 'work.read',
	workManage: 'work.manage',
	reportsRead: 'reports.read',
	moneyBalancesRead: 'money.balances.read',
	moneyDepositsManage: 'money.deposits.manage',
	moneyOwnerReportsRead: 'money.owner-reports.read',
	leasingApplicationsManage: 'leasing.applications.manage',
	leasingShowingsManage: 'leasing.showings.manage',
	leasingAgreementsPrepare: 'leasing.agreements.prepare',
	leasingOnboardingManage: 'leasing.onboarding.manage',
	leasingTermsRead: 'leasing.terms.read',
	leasingDepositsRead: 'leasing.deposits.read',
	assignedWorkRead: 'maintenance.assigned-work.read',
	assignedWorkUpdate: 'maintenance.assigned-work.update',
	assignedWorkConverse: 'maintenance.assigned-work.converse',
	teamRead: 'team.read',
	teamManage: 'team.manage',
	securityManage: 'security.manage',
	billingManage: 'billing.manage',
	integrationsManage: 'integrations.manage',
	bankConnectionsManage: 'bank-connections.manage',
	notificationsManage: 'notifications.manage'
} as const;

export interface RouteAccessRule {
	prefix: string;
	exact?: boolean;
	experiences?: readonly WorkspaceExperience[];
	anyCapabilities?: readonly string[];
}

/**
 * The shared web-shell policy. Navigation visibility and direct-route guards both consume this
 * catalog so adding a link cannot accidentally create a different authorization story from typing
 * its URL. The API remains the record-level authorization boundary.
 */
export const ROUTE_ACCESS_RULES: readonly RouteAccessRule[] = [
	{ prefix: '/', exact: true, experiences: ['Management'] },
	{
		prefix: '/docs',
		experiences: ['Management', 'Leasing', 'Maintenance', 'Owner', 'Tenant']
	},
	{ prefix: '/portal', experiences: ['Tenant'] },
	{ prefix: '/owner', experiences: ['Owner'] },
	{ prefix: '/admin/users', anyCapabilities: [CAPABILITY.teamRead, CAPABILITY.teamManage] },
	{ prefix: '/admin/audit', anyCapabilities: [CAPABILITY.reportsRead] },
	{
		prefix: '/settings/notifications/my-alerts',
		experiences: ['Management', 'Leasing', 'Maintenance', 'Owner']
	},
	{
		prefix: '/settings/notifications/team-routing',
		anyCapabilities: [CAPABILITY.notificationsManage]
	},
	{
		prefix: '/settings/notifications/tenant-notices',
		anyCapabilities: [CAPABILITY.notificationsManage]
	},
	{
		prefix: '/settings/security',
		experiences: ['Management', 'Leasing', 'Maintenance', 'Owner']
	},
	{ prefix: '/settings/accounting', anyCapabilities: [CAPABILITY.integrationsManage] },
	{
		prefix: '/settings',
		anyCapabilities: [
			CAPABILITY.securityManage,
			CAPABILITY.billingManage,
			CAPABILITY.integrationsManage
		]
	},
	{ prefix: '/onboarding', anyCapabilities: [CAPABILITY.rentalsManage] },
	{ prefix: '/get-started', anyCapabilities: [CAPABILITY.rentalsManage] },
	{ prefix: '/import', anyCapabilities: [CAPABILITY.rentalsManage] },
	{ prefix: '/banking', anyCapabilities: [CAPABILITY.bankConnectionsManage] },
	{ prefix: '/plaid', anyCapabilities: [CAPABILITY.bankConnectionsManage] },
	{ prefix: '/accounting', anyCapabilities: [CAPABILITY.moneyBalancesRead] },
	{
		prefix: '/tenant-accounts',
		anyCapabilities: [CAPABILITY.moneyBalancesRead, CAPABILITY.leasingDepositsRead]
	},
	{
		prefix: '/deposits',
		anyCapabilities: [CAPABILITY.moneyDepositsManage, CAPABILITY.leasingDepositsRead]
	},
	{
		prefix: '/reports',
		anyCapabilities: [CAPABILITY.reportsRead, CAPABILITY.moneyOwnerReportsRead]
	},
	{ prefix: '/tax', anyCapabilities: [CAPABILITY.reportsRead] },
	{ prefix: '/owners-report', anyCapabilities: [CAPABILITY.moneyOwnerReportsRead] },
	{ prefix: '/audit', anyCapabilities: [CAPABILITY.reportsRead] },
	{ prefix: '/activity', anyCapabilities: [CAPABILITY.reportsRead] },
	{ prefix: '/analytics', anyCapabilities: [CAPABILITY.reportsRead] },
	{
		prefix: '/ai',
		anyCapabilities: [CAPABILITY.rentalsRead, CAPABILITY.workRead, CAPABILITY.leasingTermsRead]
	},
	{ prefix: '/owners', anyCapabilities: [CAPABILITY.moneyOwnerReportsRead] },
	{ prefix: '/properties', anyCapabilities: [CAPABILITY.rentalsRead] },
	{ prefix: '/units', anyCapabilities: [CAPABILITY.rentalsRead] },
	{
		prefix: '/tenants',
		anyCapabilities: [CAPABILITY.rentalsRead, CAPABILITY.leasingOnboardingManage]
	},
	{
		prefix: '/leases',
		anyCapabilities: [CAPABILITY.rentalsRead, CAPABILITY.leasingTermsRead]
	},
	{
		prefix: '/lease-templates',
		anyCapabilities: [CAPABILITY.rentalsManage, CAPABILITY.leasingAgreementsPrepare]
	},
	{ prefix: '/applications', anyCapabilities: [CAPABILITY.leasingApplicationsManage] },
	{
		prefix: '/maintenance',
		anyCapabilities: [CAPABILITY.workRead, CAPABILITY.assignedWorkRead]
	},
	{
		prefix: '/appointments',
		anyCapabilities: [CAPABILITY.workRead, CAPABILITY.leasingShowingsManage]
	},
	{ prefix: '/vendors', anyCapabilities: [CAPABILITY.workManage] },
	{
		prefix: '/messages',
		anyCapabilities: [
			CAPABILITY.rentalsRead,
			CAPABILITY.leasingOnboardingManage,
			CAPABILITY.assignedWorkConverse
		]
	},
	{
		prefix: '/notices',
		anyCapabilities: [CAPABILITY.rentalsManage, CAPABILITY.leasingOnboardingManage]
	},
	{
		prefix: '/scan',
		anyCapabilities: [
			CAPABILITY.rentalsManage,
			CAPABILITY.leasingAgreementsPrepare,
			CAPABILITY.assignedWorkUpdate
		]
	}
];

export function routeAccessRule(pathname: string): RouteAccessRule | null {
	return (
		ROUTE_ACCESS_RULES.filter((rule) =>
			rule.exact ? pathname === rule.prefix : pathname === rule.prefix || pathname.startsWith(`${rule.prefix}/`)
		).sort((left, right) => right.prefix.length - left.prefix.length)[0] ?? null
	);
}

export function canAccessRoute(
	pathname: string,
	experience: WorkspaceExperience | null,
	capabilities: ReadonlySet<string>
): boolean {
	const rule = routeAccessRule(pathname);
	// Every authenticated route must be deliberately classified above. Unknown routes fail closed
	// for every experience, including full Management access.
	if (!rule) return false;
	if (rule.experiences && (!experience || !rule.experiences.includes(experience))) return false;
	if (!rule.anyCapabilities || rule.anyCapabilities.length === 0) return true;
	return rule.anyCapabilities.some((capability) => capabilities.has(capability));
}

const LANDING_CANDIDATES: Record<WorkspaceExperience, readonly string[]> = {
	Management: ['/', '/properties'],
	Leasing: ['/applications', '/appointments', '/properties', '/units', '/leases', '/messages'],
	Maintenance: ['/maintenance', '/messages'],
	Owner: ['/owner'],
	Tenant: ['/portal']
};

/** Choose the first route the selected experience can actually open. */
export function safeLandingForAccess(
	access: AccessEnvelope,
	experience: WorkspaceExperience = access.selectedContext.activeExperience
): string | null {
	const capabilities = new Set(
		access.navigation.find((entry) => entry.experience === experience)?.capabilityKeys ?? []
	);
	return (
		LANDING_CANDIDATES[experience].find((path) => {
			if (path === '/portal') return experience === 'Tenant';
			return canAccessRoute(path, experience, capabilities);
		}) ?? null
	);
}

export function canAccessPathForEnvelope(access: AccessEnvelope, pathname: string): boolean {
	const experience = access.selectedContext.activeExperience;
	const capabilities = new Set(
		access.navigation.find((entry) => entry.experience === experience)?.capabilityKeys ?? []
	);
	return canAccessRoute(pathname, experience, capabilities);
}
