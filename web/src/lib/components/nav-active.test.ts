import assert from 'node:assert/strict';
import { describe, test } from 'node:test';

import { isNavItemActive } from './nav-active.ts';

// The staff sidebar's hrefs, in the order AppShell builds them (pinned, the Units
// title entry, the four groups, the bottom rail, then the Settings hub).
const STAFF_HREFS = [
	'/',
	'/onboarding',
	'/scan',
	'/units',
	'/accounting/past-due',
	'/accounting',
	'/banking',
	'/deposits',
	'/reports',
	'/properties',
	'/owners',
	'/tenants',
	'/leases',
	'/applications',
	'/maintenance',
	'/appointments',
	'/vendors',
	'/messages',
	'/notices',
	'/ai',
	'/docs',
	'/settings/notifications',
	'/settings/lease-templates',
	'/settings',
	'/admin/users',
	'/audit'
];

/** Every sidebar href that lights up on the given address. */
function activeHrefs(currentPath: string, hrefs = STAFF_HREFS): string[] {
	return hrefs.filter((href) => isNavItemActive(currentPath, href, hrefs));
}

describe('sidebar active item', () => {
	test('lights up exactly one item per address', () => {
		assert.deepEqual(activeHrefs('/accounting/past-due'), ['/accounting/past-due']);
		assert.deepEqual(activeHrefs('/accounting'), ['/accounting']);
		assert.deepEqual(activeHrefs('/settings/lease-templates'), ['/settings/lease-templates']);
		assert.deepEqual(activeHrefs('/settings/notifications'), ['/settings/notifications']);
		assert.deepEqual(activeHrefs('/settings'), ['/settings']);
	});

	test('a deeper page still lights up the item it belongs to', () => {
		assert.deepEqual(activeHrefs('/settings/notifications/my-alerts'), ['/settings/notifications']);
		assert.deepEqual(activeHrefs('/properties/12'), ['/properties']);
	});

	test('the Units entry matches the units list and a single unit', () => {
		assert.equal(isNavItemActive('/units', '/units', STAFF_HREFS), true);
		assert.equal(isNavItemActive('/units/4', '/units', STAFF_HREFS), true);
	});

	test('the dashboard only matches the dashboard', () => {
		assert.deepEqual(activeHrefs('/'), ['/']);
		assert.equal(isNavItemActive('/tenants', '/', STAFF_HREFS), false);
	});

	test('Reports also covers the owner statement pages', () => {
		assert.equal(isNavItemActive('/owners-report', '/reports', STAFF_HREFS), true);
		assert.equal(isNavItemActive('/reports/rent-roll', '/reports', STAFF_HREFS), true);
	});

	test('an address that only looks similar does not match', () => {
		assert.equal(isNavItemActive('/owners', '/owner', ['/owner', '/owners']), false);
		assert.equal(isNavItemActive('/leases-archive', '/leases', ['/leases']), false);
	});
});
