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
