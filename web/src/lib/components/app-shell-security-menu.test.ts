import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const source = readFileSync(new URL('./AppShell.svelte', import.meta.url), 'utf8');

test('tenant-only users get a portal security link instead of the protected staff settings route', () => {
	assert.match(
		source,
		/const userSecurityHref = \$derived\(portalUser \? '\/portal\/security' : '\/settings\/security'\)/
	);
	assert.equal(source.match(/href=\{userSecurityHref\}/g)?.length, 2);
	assert.doesNotMatch(source, /href="\/settings\/security"/);
});

test('portal security contributes to shell title resolution without becoming a sidebar nav item', () => {
	const portalNavStart = source.indexOf('const portalNavItems: NavItem[] = [');
	const portalUtilityStart = source.indexOf('const portalUtilityItems: NavItem[] = [');
	const portalNavBlock = source.slice(portalNavStart, portalUtilityStart);

	assert.match(source, /const portalUtilityItems: NavItem\[\] = \[/);
	assert.match(source, /\{ href: '\/portal\/security', label: 'Security', icon: Shield \}/);
	assert.match(source, /portalUser\s+\?\s+\[\.\.\.visiblePortalNavItems, \.\.\.portalUtilityItems\]/);
	assert.doesNotMatch(portalNavBlock, /href: '\/portal\/security'/);
});

test('all five experience shells expose personal alerts with consistent alert semantics', () => {
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
		source.slice(portalNavStart, portalNavEnd),
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

	for (const relationshipNav of [
		source.slice(portalNavStart, portalNavEnd),
		source.slice(ownerNavStart, ownerNavEnd)
	]) {
		assert.doesNotMatch(relationshipNav, /team-routing|tenant-notices/);
	}
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
