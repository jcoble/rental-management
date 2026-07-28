import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const shell = readFileSync(new URL('../components/AppShell.svelte', import.meta.url), 'utf8');
const tenantAccountHub = readFileSync(
	new URL('../../routes/(portal)/portal/account/+page.svelte', import.meta.url),
	'utf8'
);
const tenantProfileHub = readFileSync(
	new URL('../../routes/(portal)/portal/profile/+page.svelte', import.meta.url),
	'utf8'
);
const ownerSecurity = readFileSync(
	new URL('../../routes/(protected)/owner/security/+page.svelte', import.meta.url),
	'utf8'
);

test('tenant peer navigation stays small without hiding account functionality', () => {
	const start = shell.indexOf('const portalNavItems: NavItem[] = [');
	const end = shell.indexOf('const portalUtilityItems: NavItem[] = [', start);
	const nav = shell.slice(start, end);

	assert.equal(nav.match(/\{ href:/g)?.length, 6);
	for (const href of [
		'/portal',
		'/portal/account',
		'/portal/maintenance',
		'/portal/appointments',
		'/portal/messages',
		'/portal/profile'
	]) {
		assert.ok(nav.includes(`href: '${href}'`), `missing tenant peer destination ${href}`);
	}
	assert.match(tenantAccountHub, /href="\/portal\/payments"/);
	assert.match(tenantAccountHub, /href="\/portal\/lease"/);
	assert.match(tenantProfileHub, /href: '\/portal\/security'/);
	assert.match(tenantProfileHub, /href: '\/settings\/notifications\/my-alerts'/);
	assert.match(tenantProfileHub, /href: '\/portal\/notifications'/);
	assert.match(tenantProfileHub, /href: '\/portal\/appointments'/);
});

test('owner relationship users can manage their own sign-in without opening workspace settings', () => {
	assert.match(shell, /ownerUser \? '\/owner\/security'/);
	assert.match(ownerSecurity, /AccountSecurityPage/);
	assert.match(ownerSecurity, /backHref="\/owner"/);
});
