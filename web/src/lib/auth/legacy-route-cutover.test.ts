import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import test from 'node:test';

const removedRouteModules = [
	['/maintenance/work-orders/[id]', '../../routes/(protected)/maintenance/work-orders/[id]/+page.ts'],
	['/activity', '../../routes/(protected)/activity/+page.ts'],
	['/owners/vendors', '../../routes/(protected)/owners/vendors/+page.ts'],
	['/owners/vendors/[id]', '../../routes/(protected)/owners/vendors/[id]/+page.ts']
] as const;

test('legacy route cutover', async (t) => {
	for (const [route, modulePath] of removedRouteModules) {
		await t.test(`${route} has no route module and resolves as 404`, () => {
			assert.equal(existsSync(new URL(modulePath, import.meta.url)), false);
		});
	}
});
