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
	assert.match(source, /portalUser\s+\?\s+\[\.\.\.portalNavItems, \.\.\.portalUtilityItems\]/);
	assert.doesNotMatch(portalNavBlock, /href: '\/portal\/security'/);
});

test('staff money navigation exposes capability-gated first-class sections', () => {
	const moneyGroupStart = source.indexOf("id: 'money'");
	const rentalsGroupStart = source.indexOf("id: 'rentals'");
	const moneyGroup = source.slice(moneyGroupStart, rentalsGroupStart);

	assert.match(moneyGroup, /label: 'Money'/);
	assert.match(moneyGroup, /\{ href: '\/accounting', label: 'Money', icon: Calculator, capabilities: \['money\.balances\.read'\] \}/);
	assert.match(moneyGroup, /\{ href: '\/deposits', label: 'Security Deposits', icon: PiggyBank, capabilities: \['money\.deposits\.manage', 'leasing\.deposits\.read'\] \}/);
	assert.match(moneyGroup, /\{ href: '\/reports', label: 'Reports', icon: BarChart3, capabilities: \['reports\.read', 'money\.owner-reports\.read'\] \}/);
	assert.doesNotMatch(moneyGroup, /roles:/);
	assert.match(source, /'\/deposits': 'savings'/);
});

test('staff users can return to setup/import from pinned nav and account menu', () => {
	assert.match(
		source,
		/\{ href: '\/onboarding', label: 'Guided Setup', icon: ClipboardList, capabilities: \['rentals\.manage'\] \}/
	);
	assert.match(source, /data-testid="user-menu-guided-setup-collapsed"/);
	assert.match(source, /data-testid="user-menu-guided-setup"/);
	assert.equal(source.match(/href="\/onboarding\?from=account-menu"/g)?.length, 2);
});
