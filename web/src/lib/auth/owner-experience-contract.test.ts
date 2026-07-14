import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const shell = readFileSync(new URL('../components/AppShell.svelte', import.meta.url), 'utf8');
const policy = readFileSync(new URL('./experience-policy.ts', import.meta.url), 'utf8');
const ownerApi = readFileSync(new URL('../api/endpoints/owner-portal.ts', import.meta.url), 'utf8');
const ownerService = readFileSync(
	new URL('../../../../RentalCommand.Api/Services/Domain/OwnerPortalService.cs', import.meta.url),
	'utf8'
);
const ownerStatementService = readFileSync(
	new URL('../../../../RentalCommand.Api/Services/Domain/OwnerStatementService.cs', import.meta.url),
	'utf8'
);

test('owner shell is a first-class relationship experience, not management navigation', () => {
	const ownerNavStart = shell.indexOf('const ownerNavItems: NavItem[] = [');
	const ownerNavEnd = shell.indexOf('const commandCenterTitleItem', ownerNavStart);
	const ownerNav = shell.slice(ownerNavStart, ownerNavEnd);

	assert.match(policy, /prefix: '\/owner', experiences: \['Owner'\]/);
	assert.match(ownerNav, /href: '\/owner'/);
	assert.match(ownerNav, /href: '\/owner\/properties'/);
	assert.match(ownerNav, /href: '\/owner\/statements'/);
	assert.match(ownerNav, /href: '\/owner\/approvals'/);
	assert.match(ownerNav, /href: '\/owner\/messages'/);
	assert.doesNotMatch(ownerNav, /admin\/users|banking|billing|settings/);
	assert.match(shell, /let relationshipUser = \$derived\(portalUser \|\| ownerUser\)/);
	assert.match(shell, /const canOpenUserSecurity = \$derived\(!ownerUser\)/);
	assert.match(shell, /\{#if !ownerUser\}[\s\S]{0,120}<NotificationBell/);
	assert.doesNotMatch(policy, /settings\/security'[\s\S]{0,100}'Owner'/);
});

test('owner clients use only the purpose-built owner API surface', () => {
	for (const path of [
		'/owner/overview',
		'/owner/properties/page',
		'/owner/statements',
		'/owner/distributions/page',
		'/owner/approvals/page',
		'/owner/messages/page'
	]) {
		assert.ok(ownerApi.includes(path), `missing owner API path ${path}`);
	}

	const requestedPaths = [...ownerApi.matchAll(/api\.get<[^>]+>\((['`])([^'`]+)\1/g)].map(
		(match) => match[2]
	);
	assert.equal(requestedPaths.length, 7);
	for (const path of requestedPaths) {
		assert.ok(path.startsWith('/owner/'), `owner client must not call management path ${path}`);
	}
});

test('owner server projections anchor every resource family to effective relationship access', () => {
	assert.match(ownerService, /_db\.EffectiveOwnerAccess/);
	assert.match(ownerService, /access\.AccessContextId == scope\.AccessContextId/);
	assert.match(ownerService, /access\.UserId == scope\.UserId/);
	assert.match(ownerService, /access\.AccessRevision == scope\.AccessRevision/);
	assert.match(ownerService, /access\.PropertyId == property\.Id/);
	assert.match(ownerService, /access\.OwnerEntityId == notification\.RelatedEntityId/);
	assert.doesNotMatch(ownerService, /ToListAsync\([^)]*\)[\s\S]{0,120}\.Where\(/);
	assert.doesNotMatch(ownerService, /foreach\s*\(/);
});

test('owner list experiences use bounded server pages instead of fixed first-page reads', () => {
	assert.match(ownerApi, /statementsPage:/);
	assert.doesNotMatch(ownerApi, /statements:\s*\(/);
	assert.match(ownerStatementService, /ListForOwnerPortalPageAsync/);
	assert.match(ownerStatementService, /\.Skip\(query\.NormalizedSkip\)/);
	assert.match(ownerStatementService, /\.Take\(query\.NormalizedTake\)/);
	assert.match(ownerStatementService, /\.OrderBy\(summary => summary\.OwnerName\)/);
});
