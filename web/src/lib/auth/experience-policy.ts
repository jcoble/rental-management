import type { AccessEnvelope, WorkspaceExperience } from '$lib/types/user';

export const CAPABILITY = {
	rentalsRead: 'rentals.read',
	rentalsManage: 'rentals.manage',
	workRead: 'work.read',
	workManage: 'work.manage',
	reportsRead: 'reports.read',
	moneyBalancesRead: 'money.balances.read',
	moneyPaymentsManage: 'money.payments.manage',
	moneyExpensesManage: 'money.expenses.manage',
	moneyDepositsManage: 'money.deposits.manage',
	moneyOwnerReportsRead: 'money.owner-reports.read',
	moneyReconciliationOperate: 'money.reconciliation.operate',
	moneyReconciliationDestructive: 'money.reconciliation.destructive',
	leasingApplicationsManage: 'leasing.applications.manage',
	leasingListingsManage: 'leasing.listings.manage',
	leasingShowingsManage: 'leasing.showings.manage',
	leasingAgreementsPrepare: 'leasing.agreements.prepare',
	leasingOnboardingManage: 'leasing.onboarding.manage',
	leasingTermsRead: 'leasing.terms.read',
	leasingDepositsRead: 'leasing.deposits.read',
	assignedWorkRead: 'maintenance.assigned-work.read',
	assignedWorkUpdate: 'maintenance.assigned-work.update',
	assignedWorkConverse: 'maintenance.assigned-work.converse',
	assignedWorkTimeMaterialsManage: 'maintenance.assigned-work.time-materials.manage',
	teamRead: 'team.read',
	teamManage: 'team.manage',
	securityManage: 'security.manage',
	billingManage: 'billing.manage',
	integrationsManage: 'integrations.manage',
	bankConnectionsManage: 'bank-connections.manage',
	notificationsManage: 'notifications.manage',
	tenantNoticesManage: 'notifications.tenant-notices.manage'
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
	{
		prefix: '/leasing/applications',
		experiences: ['Leasing'],
		anyCapabilities: [CAPABILITY.leasingApplicationsManage]
	},
	{
		prefix: '/leasing/appointments',
		experiences: ['Leasing'],
		anyCapabilities: [CAPABILITY.leasingShowingsManage]
	},
	{
		prefix: '/leasing/conversations',
		experiences: ['Leasing'],
		anyCapabilities: [CAPABILITY.leasingOnboardingManage]
	},
	{
		prefix: '/leasing/move-ins',
		experiences: ['Leasing'],
		anyCapabilities: [CAPABILITY.leasingOnboardingManage]
	},
	{
		prefix: '/leasing/rentals',
		experiences: ['Leasing'],
		anyCapabilities: [CAPABILITY.leasingListingsManage]
	},
	{
		prefix: '/leasing/pipeline',
		experiences: ['Leasing'],
		anyCapabilities: [
			CAPABILITY.leasingApplicationsManage,
			CAPABILITY.leasingAgreementsPrepare,
			CAPABILITY.leasingOnboardingManage
		]
	},
	{
		prefix: '/leasing/calendar',
		experiences: ['Leasing'],
		anyCapabilities: [CAPABILITY.leasingShowingsManage]
	},
	{
		prefix: '/leasing/inbox',
		experiences: ['Leasing'],
		anyCapabilities: [CAPABILITY.leasingOnboardingManage]
	},
	{
		prefix: '/leasing',
		experiences: ['Leasing'],
		anyCapabilities: [
			CAPABILITY.leasingApplicationsManage,
			CAPABILITY.leasingListingsManage,
			CAPABILITY.leasingShowingsManage,
			CAPABILITY.leasingAgreementsPrepare,
			CAPABILITY.leasingOnboardingManage,
			CAPABILITY.leasingTermsRead,
			CAPABILITY.leasingDepositsRead
		]
	},
	{
		prefix: '/my-work',
		experiences: ['Maintenance'],
		anyCapabilities: [CAPABILITY.assignedWorkRead]
	},
	{
		prefix: '/my-schedule',
		experiences: ['Maintenance'],
		anyCapabilities: [CAPABILITY.assignedWorkRead]
	},
	{
		prefix: '/assignment-inbox',
		experiences: ['Maintenance'],
		anyCapabilities: [CAPABILITY.assignedWorkConverse]
	},
	{ prefix: '/profile', experiences: ['Leasing', 'Maintenance'] },
	// Leasing has purpose-built work queues and Maintenance has assignment-scoped routes. Do not
	// expose broad management list or detail screens as alternate shells for either experience.
	{ prefix: '/properties', exact: true, experiences: ['Management'], anyCapabilities: [CAPABILITY.rentalsRead] },
	{ prefix: '/units', exact: true, experiences: ['Management'], anyCapabilities: [CAPABILITY.rentalsRead] },
	{ prefix: '/tenants', exact: true, experiences: ['Management'], anyCapabilities: [CAPABILITY.rentalsRead] },
	{ prefix: '/leases', exact: true, experiences: ['Management'], anyCapabilities: [CAPABILITY.rentalsRead] },
	{ prefix: '/applications', exact: true, experiences: ['Management'], anyCapabilities: [CAPABILITY.leasingApplicationsManage] },
	{ prefix: '/appointments', exact: true, experiences: ['Management'], anyCapabilities: [CAPABILITY.workRead, CAPABILITY.leasingShowingsManage] },
	{
		prefix: '/messages',
		exact: true,
		experiences: ['Management'],
		anyCapabilities: [CAPABILITY.rentalsRead, CAPABILITY.leasingOnboardingManage]
	},
	{ prefix: '/admin/users', anyCapabilities: [CAPABILITY.teamRead, CAPABILITY.teamManage] },
	{ prefix: '/admin/audit', anyCapabilities: [CAPABILITY.reportsRead] },
	// Personal alert destinations belong to the signed-in user in every experience. Administrative
	// routing and tenant-delivery policy remain staff-only even if a stale relationship envelope
	// happens to contain the workspace notification capability.
	{
		prefix: '/settings/notifications/my-alerts',
		experiences: ['Management', 'Leasing', 'Maintenance', 'Owner', 'Tenant']
	},
	{
		prefix: '/settings/notifications/team-routing',
		experiences: ['Management', 'Leasing', 'Maintenance'],
		anyCapabilities: [CAPABILITY.notificationsManage]
	},
	{
		prefix: '/settings/notifications/tenant-notices',
		experiences: ['Management', 'Leasing', 'Maintenance'],
		anyCapabilities: [CAPABILITY.tenantNoticesManage]
	},
	{
		prefix: '/settings/security',
		experiences: ['Management', 'Leasing', 'Maintenance']
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
	{
		prefix: '/banking',
		anyCapabilities: [CAPABILITY.moneyReconciliationOperate, CAPABILITY.bankConnectionsManage]
	},
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
	{ prefix: '/properties', experiences: ['Management'], anyCapabilities: [CAPABILITY.rentalsRead] },
	{ prefix: '/units', experiences: ['Management'], anyCapabilities: [CAPABILITY.rentalsRead] },
	{
		prefix: '/tenants',
		experiences: ['Management'],
		anyCapabilities: [CAPABILITY.rentalsRead, CAPABILITY.leasingOnboardingManage]
	},
	{
		prefix: '/leases',
		experiences: ['Management'],
		anyCapabilities: [CAPABILITY.rentalsRead, CAPABILITY.leasingTermsRead]
	},
	{
		prefix: '/lease-templates',
		anyCapabilities: [CAPABILITY.rentalsManage, CAPABILITY.leasingAgreementsPrepare]
	},
	{
		prefix: '/applications',
		experiences: ['Management'],
		anyCapabilities: [CAPABILITY.leasingApplicationsManage]
	},
	{
		prefix: '/maintenance',
		experiences: ['Management'],
		anyCapabilities: [CAPABILITY.workRead]
	},
	{
		prefix: '/appointments',
		experiences: ['Management'],
		anyCapabilities: [CAPABILITY.workRead, CAPABILITY.leasingShowingsManage]
	},
	{ prefix: '/vendors', experiences: ['Management'], anyCapabilities: [CAPABILITY.workManage] },
	{
		prefix: '/messages',
		experiences: ['Management'],
		anyCapabilities: [
			CAPABILITY.rentalsRead,
			CAPABILITY.leasingOnboardingManage
		]
	},
	{
		prefix: '/notices',
		experiences: ['Management', 'Leasing'],
		anyCapabilities: [CAPABILITY.tenantNoticesManage]
	},
	{
		prefix: '/scan',
		experiences: ['Management', 'Leasing', 'Maintenance'],
		anyCapabilities: [
			CAPABILITY.rentalsManage,
			CAPABILITY.moneyExpensesManage,
			CAPABILITY.moneyPaymentsManage,
			CAPABILITY.workManage,
			CAPABILITY.leasingAgreementsPrepare,
			CAPABILITY.leasingApplicationsManage,
			CAPABILITY.assignedWorkUpdate
		]
	}
];

export function routeAccessRule(pathname: string): RouteAccessRule | null {
	return (
		ROUTE_ACCESS_RULES.filter((rule) =>
			rule.exact ? pathname === rule.prefix : pathname === rule.prefix || pathname.startsWith(`${rule.prefix}/`)
		).sort(
			(left, right) =>
				right.prefix.length - left.prefix.length ||
				Number(right.exact ?? false) - Number(left.exact ?? false)
		)[0] ?? null
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
	Leasing: ['/leasing'],
	Maintenance: ['/my-work', '/my-schedule', '/assignment-inbox'],
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
