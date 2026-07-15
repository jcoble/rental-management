import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { describe, it } from 'node:test';
import { canAccessRoute, CAPABILITY, routeAccessRule } from './experience-policy.ts';

const protectedRouteRoot = new URL('../../routes/(protected)', import.meta.url);

describe('experience route policy', () => {
	it('classifies every top-level protected route prefix', () => {
		const prefixes = readdirSync(protectedRouteRoot, { withFileTypes: true })
			.filter((entry) => entry.isDirectory())
			.map((entry) => `/${entry.name}`)
			.sort();

		for (const prefix of prefixes) {
			assert.ok(routeAccessRule(prefix), `${prefix} must have an explicit route policy`);
		}
	});

	it('denies unclassified routes to every experience', () => {
		const noCapabilities = new Set<string>();
		assert.equal(canAccessRoute('/future-protected-route', 'Leasing', noCapabilities), false);
		assert.equal(canAccessRoute('/future-protected-route', 'Maintenance', noCapabilities), false);
		assert.equal(canAccessRoute('/future-protected-route', 'Owner', noCapabilities), false);
		assert.equal(canAccessRoute('/future-protected-route', 'Management', noCapabilities), false);
	});

	it('keeps Leasing in its purpose-built work area and record details', () => {
		const capabilities = new Set<string>([
			CAPABILITY.rentalsRead,
			CAPABILITY.leasingApplicationsManage,
			CAPABILITY.leasingListingsManage,
			CAPABILITY.leasingShowingsManage
		]);
		assert.equal(canAccessRoute('/leasing', 'Leasing', capabilities), true);
		assert.equal(canAccessRoute('/leasing/pipeline', 'Leasing', capabilities), true);
		assert.equal(canAccessRoute('/applications', 'Leasing', capabilities), false);
		assert.equal(canAccessRoute('/applications/42', 'Leasing', capabilities), false);
		assert.equal(canAccessRoute('/leasing/applications/42', 'Leasing', capabilities), true);
		assert.equal(canAccessRoute('/properties', 'Leasing', capabilities), false);
		assert.equal(canAccessRoute('/properties/12', 'Leasing', capabilities), false);
		assert.equal(canAccessRoute('/leasing/rentals/12', 'Leasing', capabilities), true);
		assert.equal(canAccessRoute('/profile', 'Leasing', capabilities), true);
		assert.equal(canAccessRoute('/notices', 'Leasing', capabilities), false);
		capabilities.add(CAPABILITY.leasingOnboardingManage);
		assert.equal(canAccessRoute('/messages/42', 'Leasing', capabilities), true);
		assert.equal(canAccessRoute('/messages', 'Leasing', capabilities), false);
		capabilities.add(CAPABILITY.tenantNoticesManage);
		assert.equal(canAccessRoute('/notices', 'Leasing', capabilities), true);
		assert.equal(canAccessRoute('/accounting', 'Leasing', capabilities), false);
		assert.equal(canAccessRoute('/owner', 'Leasing', capabilities), false);
	});

	it('allows Maintenance assignment routes and denies general rental browsing', () => {
		const capabilities = new Set<string>([
			CAPABILITY.assignedWorkRead,
			CAPABILITY.assignedWorkConverse
		]);
		assert.equal(canAccessRoute('/my-work', 'Maintenance', capabilities), true);
		assert.equal(canAccessRoute('/my-work/42', 'Maintenance', capabilities), true);
		assert.equal(canAccessRoute('/my-schedule', 'Maintenance', capabilities), true);
		assert.equal(canAccessRoute('/assignment-inbox', 'Maintenance', capabilities), true);
		assert.equal(canAccessRoute('/messages/42', 'Maintenance', capabilities), true);
		assert.equal(canAccessRoute('/profile', 'Maintenance', capabilities), true);
		assert.equal(canAccessRoute('/maintenance/42', 'Maintenance', capabilities), false);
		assert.equal(canAccessRoute('/messages', 'Maintenance', capabilities), false);
		assert.equal(canAccessRoute('/scan', 'Maintenance', capabilities), false);
		capabilities.add(CAPABILITY.assignedWorkUpdate);
		assert.equal(canAccessRoute('/scan', 'Maintenance', capabilities), true);
		assert.equal(canAccessRoute('/units', 'Maintenance', capabilities), false);
		assert.equal(canAccessRoute('/accounting', 'Maintenance', capabilities), false);
	});

	it('keeps Owner in the purpose-built shell and out of Management routes', () => {
		const noCapabilities = new Set<string>();
		assert.equal(canAccessRoute('/owner', 'Owner', noCapabilities), true);
		assert.equal(canAccessRoute('/settings/security', 'Owner', noCapabilities), false);
		assert.equal(canAccessRoute('/settings/notifications/my-alerts', 'Owner', noCapabilities), true);
		assert.equal(canAccessRoute('/properties', 'Owner', noCapabilities), false);
		assert.equal(canAccessRoute('/', 'Owner', noCapabilities), false);
	});

	it('keeps personal alerts available while capability-gating notification administration', () => {
		const noCapabilities = new Set<string>();
		const teamRoutingAdministrator = new Set([CAPABILITY.notificationsManage]);
		const tenantNoticeAdministrator = new Set([CAPABILITY.tenantNoticesManage]);
		const completeNotificationAdministrator = new Set([
			CAPABILITY.notificationsManage,
			CAPABILITY.tenantNoticesManage
		]);
		const staffExperiences = ['Management', 'Leasing', 'Maintenance'] as const;
		const relationshipExperiences = ['Owner', 'Tenant'] as const;

		for (const experience of ['Management', 'Leasing', 'Maintenance', 'Owner', 'Tenant'] as const) {
			assert.equal(
				canAccessRoute('/settings/notifications/my-alerts', experience, noCapabilities),
				true
			);
			assert.equal(
				canAccessRoute('/settings/notifications/team-routing', experience, noCapabilities),
				false
			);
			assert.equal(
				canAccessRoute('/settings/notifications/tenant-notices', experience, noCapabilities),
				false
			);
		}

		for (const experience of staffExperiences) {
			assert.equal(canAccessRoute('/settings/notifications/team-routing', experience, teamRoutingAdministrator), true);
			assert.equal(canAccessRoute('/settings/notifications/tenant-notices', experience, teamRoutingAdministrator), false);
			assert.equal(canAccessRoute('/settings/notifications/team-routing', experience, tenantNoticeAdministrator), false);
			assert.equal(canAccessRoute('/settings/notifications/tenant-notices', experience, tenantNoticeAdministrator), true);
		}

		for (const experience of relationshipExperiences) {
			assert.equal(
				canAccessRoute('/settings/notifications/team-routing', experience, completeNotificationAdministrator),
				false
			);
			assert.equal(
				canAccessRoute('/settings/notifications/tenant-notices', experience, completeNotificationAdministrator),
				false
			);
		}
	});

	it('uses exact capability gates for purpose-built Leasing and Maintenance destinations', () => {
		const leasingMatrix = [
			[CAPABILITY.leasingApplicationsManage, '/leasing/applications/12'],
			[CAPABILITY.leasingListingsManage, '/leasing/rentals'],
			[CAPABILITY.leasingShowingsManage, '/leasing/calendar'],
			[CAPABILITY.leasingOnboardingManage, '/leasing/inbox']
		] as const;
		for (const [capability, path] of leasingMatrix) {
			assert.equal(canAccessRoute(path, 'Leasing', new Set([capability])), true, `${path} must accept ${capability}`);
			assert.equal(canAccessRoute(path, 'Leasing', new Set()), false, `${path} must fail closed without ${capability}`);
		}

		assert.equal(canAccessRoute('/my-work/12', 'Maintenance', new Set([CAPABILITY.assignedWorkRead])), true);
		assert.equal(canAccessRoute('/my-work/12', 'Maintenance', new Set([CAPABILITY.assignedWorkUpdate])), false);
		assert.equal(canAccessRoute('/assignment-inbox', 'Maintenance', new Set([CAPABILITY.assignedWorkConverse])), true);
		assert.equal(canAccessRoute('/assignment-inbox', 'Maintenance', new Set([CAPABILITY.assignedWorkRead])), false);
	});

	it('keeps six representative personas in their own navigation boundaries', () => {
		const personas = [
			{ name: 'workspace administrator', experience: 'Management', caps: [CAPABILITY.securityManage, CAPABILITY.rentalsManage, CAPABILITY.rentalsRead], allowed: '/settings', denied: '/leasing' },
			{ name: 'property manager', experience: 'Management', caps: [CAPABILITY.rentalsRead, CAPABILITY.workRead, CAPABILITY.moneyBalancesRead], allowed: '/properties', denied: '/settings' },
			{ name: 'leasing agent', experience: 'Leasing', caps: [CAPABILITY.leasingApplicationsManage], allowed: '/leasing/pipeline', denied: '/properties' },
			{ name: 'maintenance technician', experience: 'Maintenance', caps: [CAPABILITY.assignedWorkRead], allowed: '/my-work', denied: '/maintenance' },
			{ name: 'owner', experience: 'Owner', caps: [], allowed: '/owner', denied: '/' },
			{ name: 'tenant', experience: 'Tenant', caps: [], allowed: '/portal', denied: '/owner' }
		] as const;

		for (const persona of personas) {
			const capabilities = new Set<string>(persona.caps);
			assert.equal(canAccessRoute(persona.allowed, persona.experience, capabilities), true, `${persona.name} expected ${persona.allowed}`);
			assert.equal(canAccessRoute(persona.denied, persona.experience, capabilities), false, `${persona.name} must not open ${persona.denied}`);
		}
	});

	it('splits operational reconciliation from bank administration', () => {
		const propertyManager = new Set([CAPABILITY.moneyReconciliationOperate]);
		const bankAdministrator = new Set([CAPABILITY.bankConnectionsManage]);

		assert.equal(canAccessRoute('/banking', 'Management', propertyManager), true);
		assert.equal(canAccessRoute('/plaid', 'Management', propertyManager), false);
		assert.equal(canAccessRoute('/banking', 'Management', bankAdministrator), true);
		assert.equal(canAccessRoute('/plaid', 'Management', bankAdministrator), true);
	});

	it('keeps private bank queries and destructive controls behind exact capabilities', () => {
		const bankingPage = readFileSync(
			new URL('../../routes/(protected)/banking/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(bankingPage, /enabled: !!portfolioId && canManageConnections/g);
		assert.match(bankingPage, /enabled: !!portfolioId && canOperateReconciliation/);
		assert.match(bankingPage, /\{#if canDestructivelyReconcile\}/);
		assert.match(bankingPage, /\{#if canManageConnections\}[\s\S]*Connect Plaid/);
	});
});
