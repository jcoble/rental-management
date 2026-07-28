import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';
import { canAccessRoute } from '../auth/experience-policy.ts';

const appShell = readFileSync(new URL('./AppShell.svelte', import.meta.url), 'utf8');
const commandCenterNav = readFileSync(
	new URL('./CommandCenterNav.svelte', import.meta.url),
	'utf8'
);
const mobileDestination = readFileSync(
	new URL('../../../../mobile/lib/features/home/mobile_destination.dart', import.meta.url),
	'utf8'
);
const mobileHomeShell = readFileSync(
	new URL('../../../../mobile/lib/features/home/home_shell.dart', import.meta.url),
	'utf8'
);

describe('tenant shell hierarchy', () => {
	test('keeps mobile tenant hierarchy compact and exposes web appointment navigation', () => {
		const declarationStart = mobileDestination.indexOf('const tenantShellDestinations');
		const declarationEnd = mobileDestination.indexOf(
			'const gettingStartedDestination',
			declarationStart
		);
		const declaration = mobileDestination.slice(declarationStart, declarationEnd);
		const labels = [...declaration.matchAll(/label: '([^']+)'/g)].map((match) => match[1]);

		assert.deepEqual(labels, [
			'Home',
			'Account & lease',
			'Maintenance',
			'Messages',
			'Profile'
		]);

		const shellStart = mobileHomeShell.indexOf(
			'if (authState.activeExperience == WorkspaceExperience.tenant)'
		);
		const roleShellStart = mobileHomeShell.indexOf('child: MobileRoleShell(', shellStart);
		const shellEnd = mobileHomeShell.indexOf('final user = authState.user;', roleShellStart);
		const tenantShell = mobileHomeShell.slice(shellStart, shellEnd);
		assert.equal(tenantShell.match(/MobileRoleDestination\(/g)?.length, 5);
		assert.doesNotMatch(tenantShell, /TabBar|TabBarView|SegmentedButton|NavigationRail/);

		const tenantComponentsStart = mobileHomeShell.indexOf('class _TenantAccountLeaseTab');
		const tenantComponentsEnd = mobileHomeShell.indexOf(
			'class _TenantCard',
			tenantComponentsStart
		);
		const tenantComponents = mobileHomeShell.slice(tenantComponentsStart, tenantComponentsEnd);
		assert.doesNotMatch(
			tenantComponents,
			/TabBar|TabBarView|SegmentedButton|CupertinoSegmentedControl/
		);

		const portalStart = appShell.indexOf('const portalNavItems: NavItem[] = [');
		const portalEnd = appShell.indexOf('const portalUtilityItems: NavItem[] = [', portalStart);
		const portalNav = appShell.slice(portalStart, portalEnd);
		assert.equal(portalNav.match(/\{ href:/g)?.length, 6);
		assert.match(portalNav, /href: '\/portal\/appointments'/);
		assert.doesNotMatch(portalNav, /role="tablist"|aria-selected|data-tabs/);
	});

	test('keeps restricted projections gated', () => {
		const capabilities = new Set([
			'rentals.read',
			'money.balances.read',
			'work.read'
		]);

		for (const route of ['/units', '/units/42', '/tenant-accounts/7/entries/9', '/accounting']) {
			assert.equal(canAccessRoute(route, 'Tenant', capabilities), false, route);
		}
		for (const route of [
			'/portal',
			'/portal/account',
			'/portal/maintenance',
			'/portal/appointments',
			'/portal/messages',
			'/portal/profile'
		]) {
			assert.equal(canAccessRoute(route, 'Tenant', capabilities), true, route);
		}
	});
});

describe('management rentals hierarchy', () => {
	test('keeps global Rentals lists and restores the server-paged Command Center picker', () => {
		const rentalsStart = appShell.indexOf("id: 'rentals'");
		const workStart = appShell.indexOf("id: 'work'", rentalsStart);
		const rentalsGroup = appShell.slice(rentalsStart, workStart);

		assert.match(rentalsGroup, /label: 'Rentals'/);
		assert.match(rentalsGroup, /\{ href: '\/units', label: 'Units', icon: Home \}/);
		assert.match(appShell, /import CommandCenterNav/);
		assert.match(appShell, /<CommandCenterNav/);
		assert.match(commandCenterNav, /createQuery|listWithHealthPage|take:\s*20|#each\s+matchedUnits/);
		assert.match(commandCenterNav, /search:\s*debouncedSearch\.value/);
		assert.match(commandCenterNav, /enabled:\s*portfolioId > 0 && open && !collapsed/);
		assert.doesNotMatch(commandCenterNav, /\.filter\(/);
		assert.match(commandCenterNav, />Command Center</);
		assert.match(
			appShell,
			/if \(href === '\/units'\) return currentPath === '\/units' \|\| currentPath\.startsWith\('\/units\/'\)/
		);
		assert.match(commandCenterNav, /staleTime:\s*5 \* 60 \* 1000/);
		assert.match(
			appShell,
			/!currentPath\.startsWith\('\/settings\/notifications\/'\)/
		);
	});

	test('preserves canonical deep links', () => {
		const managementCapabilities = new Set(['rentals.read']);

		assert.equal(canAccessRoute('/units', 'Management', managementCapabilities), true);
		assert.equal(canAccessRoute('/units/42', 'Management', managementCapabilities), true);
		assert.equal(canAccessRoute('/units/42/tenant-lease', 'Management', managementCapabilities), true);
		assert.equal(canAccessRoute('/units/42', 'Tenant', managementCapabilities), false);
		assert.match(
			appShell,
			/const commandCenterTitleItem: NavItem = \{ href: '\/units\/', label: 'Command Center', icon: Home \}/
		);
	});
});
