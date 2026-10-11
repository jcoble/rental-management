import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { dashboardActivityHref, resolveDashboardActivityUnitHref } from '../../lib/navigation/dashboard-activity-href.ts';
import type { DashboardActivity } from '../../lib/types/index.ts';

const activity = (overrides: Partial<DashboardActivity>): DashboardActivity => ({
	id: 1,
	type: 'LeaseAgreement',
	entityId: 19,
	unitId: 42,
	description: 'Lease updated',
	action: 'Updated',
	label: 'Unit 42',
	createdAt: '2026-08-09T12:00:00Z',
	...overrides,
});

describe('dashboard activity deep links', () => {
	it('routes a lease agreement to its owning unit leasing tab', () => {
		const href = dashboardActivityHref(activity({
			type: 'LeaseAgreement',
			entityId: 77,
			leaseManagementId: 19,
			unitId: 42,
		}));
		assert.equal(href, '/units/42?tab=tenant-lease&view=agreements&leaseManagement=19');
		assert.deepEqual(resolveDashboardActivityUnitHref(href!), {
			destination: { tab: 'tenant-lease', view: 'agreements' },
			hasExplicitViewOrRecord: true,
		});
	});

	it('routes a tenant account to its owning unit tenant-account view', () => {
		const href = dashboardActivityHref(activity({ type: 'TenantAccount', entityId: 88, unitId: 42 }));
		assert.equal(href, '/units/42?tab=money&view=tenant-account&tenantAccount=88');
		assert.deepEqual(resolveDashboardActivityUnitHref(href!), {
			destination: { tab: 'money', view: 'tenant-account' },
			hasExplicitViewOrRecord: true,
		});
	});

	it('falls back to no link when the owning unit is missing', () => {
		assert.equal(dashboardActivityHref(activity({ type: 'LeaseAgreement', unitId: null })), null);
		assert.equal(dashboardActivityHref(activity({ type: 'TenantAccount', unitId: null })), null);
		assert.equal(dashboardActivityHref(activity({ type: 'TenantAccount', entityId: null as unknown as number })), null);
	});
});
