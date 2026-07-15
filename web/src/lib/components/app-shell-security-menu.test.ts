import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const source = readFileSync(new URL('./AppShell.svelte', import.meta.url), 'utf8');

test('relationship users get purpose-built security routes instead of staff settings', () => {
	assert.match(
		source,
		/portalUser \? '\/portal\/security' : ownerUser \? '\/owner\/security' : '\/settings\/security'/
	);
	assert.match(source, /const canOpenUserSecurity = \$derived\(true\)/);
	assert.equal(source.match(/href=\{userSecurityHref\}/g)?.length, 2);
	assert.doesNotMatch(source, /href="\/settings\/security"/);
});

test('tenant shell uses five plain-language destinations while subpages retain title context', () => {
	const portalNavStart = source.indexOf('const portalNavItems: NavItem[] = [');
	const portalUtilityStart = source.indexOf('const portalUtilityItems: NavItem[] = [');
	const portalNavBlock = source.slice(portalNavStart, portalUtilityStart);

	assert.match(source, /const portalUtilityItems: NavItem\[\] = \[/);
	for (const label of ['Home', 'Account & lease', 'Maintenance', 'Messages', 'Profile']) {
		assert.match(portalNavBlock, new RegExp(`label: '${label}'`));
	}
	assert.equal(portalNavBlock.match(/\{ href:/g)?.length, 5);
	assert.match(source, /\{ href: '\/portal\/security', label: 'Profile', icon: Shield \}/);
	assert.match(source, /portalUser\s+\?\s+\[\.\.\.visiblePortalNavItems, \.\.\.portalUtilityItems\]/);
	assert.doesNotMatch(portalNavBlock, /href: '\/portal\/security'/);
});

test('all five experience shells keep personal alerts discoverable without exposing administration', () => {
	const portalNavStart = source.indexOf('const portalNavItems: NavItem[] = [');
	const portalNavEnd = source.indexOf('const portalUtilityItems: NavItem[] = [', portalNavStart);
	const ownerNavStart = source.indexOf('const ownerNavItems: NavItem[] = [');
	const ownerNavEnd = source.indexOf('const leasingNavItems: NavItem[] = [', ownerNavStart);
	const leasingNavStart = ownerNavEnd;
	const leasingNavEnd = source.indexOf('const technicianNavItems: NavItem[] = [', leasingNavStart);
	const technicianNavStart = leasingNavEnd;
	const technicianNavEnd = source.indexOf('const commandCenterTitleItem', technicianNavStart);
	const settingsGroupStart = source.indexOf("id: 'settings'");
	const settingsGroupEnd = source.indexOf('const navGlyphByHref', settingsGroupStart);

	for (const nav of [
		source.slice(ownerNavStart, ownerNavEnd),
		source.slice(leasingNavStart, leasingNavEnd),
		source.slice(technicianNavStart, technicianNavEnd),
		source.slice(settingsGroupStart, settingsGroupEnd)
	]) {
		assert.match(
			nav,
			/\{ href: '\/settings\/notifications\/my-alerts', label: 'My alerts', icon: BellRing \}/
		);
	}
	assert.match(source, /\{ href: '\/settings\/notifications\/my-alerts', label: 'Profile', icon: BellRing \}/);

	for (const relationshipNav of [
		source.slice(portalNavStart, portalNavEnd),
		source.slice(ownerNavStart, ownerNavEnd)
	]) {
		assert.doesNotMatch(relationshipNav, /team-routing|tenant-notices/);
	}

	assert.match(source.slice(leasingNavStart, leasingNavEnd), /href: '\/notices', label: 'Tenant notices'/);
	assert.doesNotMatch(source.slice(leasingNavStart, leasingNavEnd), /team-routing|settings\/notifications\/tenant-notices/);
	assert.doesNotMatch(source.slice(technicianNavStart, technicianNavEnd), /team-routing|tenant-notices/);
});

test('staff money navigation exposes capability-gated first-class sections', () => {
	const moneyGroupStart = source.indexOf("id: 'money'");
	const rentalsGroupStart = source.indexOf("id: 'rentals'");
	const moneyGroup = source.slice(moneyGroupStart, rentalsGroupStart);

	assert.match(moneyGroup, /label: 'Money'/);
	assert.match(moneyGroup, /\{ href: '\/accounting', label: 'Money', icon: Calculator \}/);
	assert.match(moneyGroup, /\{ href: '\/deposits', label: 'Security Deposits', icon: PiggyBank \}/);
	assert.match(moneyGroup, /\{ href: '\/reports', label: 'Reports', icon: BarChart3 \}/);
	assert.doesNotMatch(moneyGroup, /roles:/);
	assert.match(source, /canAccessRoute\(item\.href, activeExperience, activeCapabilities\)/);
	assert.match(source, /'\/deposits': 'savings'/);
});

test('staff users can return to setup/import from pinned nav and account menu', () => {
	assert.match(
		source,
		/\{ href: '\/onboarding', label: 'Guided Setup', icon: ClipboardList \}/
	);
	assert.match(source, /canOpenGuidedSetup = \$derived\([\s\S]*canAccessRoute\('\/onboarding', activeExperience, activeCapabilities\)/);
	assert.match(source, /data-testid="user-menu-guided-setup-collapsed"/);
	assert.match(source, /data-testid="user-menu-guided-setup"/);
	assert.equal(source.match(/href="\/onboarding\?from=account-menu"/g)?.length, 2);
});

test('every experience renders only route-authorized navigation and restricted shells avoid management counters', () => {
	for (const visibleList of [
		'visiblePortalNavItems',
		'visibleOwnerNavItems',
		'visibleLeasingNavItems',
		'visibleTechnicianNavItems'
	]) {
		assert.match(source, new RegExp(`\\{#each ${visibleList} as item\\}`));
	}
	assert.match(source, /enabled: isManagementSession && canOpenHeaderMessages/);
	assert.match(source, /enabled: isManagementSession && canOpenHeaderAppointments/);
	assert.match(source, /enabled: isLeasingSession && \(canOpenHeaderMessages \|\| canOpenHeaderAppointments\)/);
	assert.match(source, /allowedTypes=\{allowedHeaderScanTypes\}/);
	assert.match(source, /allowVoice=\{allowHeaderVoiceCapture\}/);
});
