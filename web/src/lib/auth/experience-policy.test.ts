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

	it('allows Leasing work routes and denies financial and Owner routes', () => {
		const capabilities = new Set([
			CAPABILITY.rentalsRead,
			CAPABILITY.leasingApplicationsManage,
			CAPABILITY.leasingShowingsManage
		]);
		assert.equal(canAccessRoute('/applications', 'Leasing', capabilities), true);
		assert.equal(canAccessRoute('/properties/12', 'Leasing', capabilities), true);
		assert.equal(canAccessRoute('/notices', 'Leasing', capabilities), false);
		capabilities.add(CAPABILITY.tenantNoticesManage);
		assert.equal(canAccessRoute('/notices', 'Leasing', capabilities), true);
		assert.equal(canAccessRoute('/accounting', 'Leasing', capabilities), false);
		assert.equal(canAccessRoute('/owner', 'Leasing', capabilities), false);
	});

	it('allows Maintenance assignment routes and denies general rental browsing', () => {
		const capabilities = new Set([
			CAPABILITY.assignedWorkRead,
			CAPABILITY.assignedWorkConverse
		]);
		assert.equal(canAccessRoute('/maintenance/42', 'Maintenance', capabilities), true);
		assert.equal(canAccessRoute('/messages', 'Maintenance', capabilities), true);
		assert.equal(canAccessRoute('/scan', 'Maintenance', capabilities), false);
		assert.equal(canAccessRoute('/units', 'Maintenance', capabilities), false);
		assert.equal(canAccessRoute('/accounting', 'Maintenance', capabilities), false);
	});

	it('keeps Owner in the purpose-built shell and out of Management routes', () => {
		const noCapabilities = new Set<string>();
		assert.equal(canAccessRoute('/owner', 'Owner', noCapabilities), true);
		assert.equal(canAccessRoute('/settings/security', 'Owner', noCapabilities), true);
		assert.equal(canAccessRoute('/properties', 'Owner', noCapabilities), false);
		assert.equal(canAccessRoute('/', 'Owner', noCapabilities), false);
	});

	it('keeps personal alerts available while capability-gating notification administration', () => {
		const noCapabilities = new Set<string>();
		const notificationAdministrator = new Set([CAPABILITY.notificationsManage]);

		for (const experience of ['Management', 'Leasing', 'Maintenance', 'Owner'] as const) {
			assert.equal(
				canAccessRoute('/settings/notifications/my-alerts', experience, noCapabilities),
				true
			);
			assert.equal(
				canAccessRoute('/settings/notifications/team-routing', experience, noCapabilities),
				false
			);
		}

		assert.equal(
			canAccessRoute('/settings/notifications/team-routing', 'Management', notificationAdministrator),
			true
		);
		assert.equal(
			canAccessRoute('/settings/notifications/tenant-notices', 'Management', notificationAdministrator),
			true
		);
		assert.equal(canAccessRoute('/settings/notifications/my-alerts', 'Tenant', noCapabilities), false);
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
