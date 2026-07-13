import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const protectedLayout = readFileSync(
	new URL('../../routes/(protected)/+layout.server.ts', import.meta.url),
	'utf8'
);

test('direct staff routes use the same role-capability gates as navigation', () => {
	assert.match(protectedLayout, /\['\/admin\/users', \['team\.read', 'team\.manage'\]\]/);
	assert.match(
		protectedLayout,
		/\['\/settings', \['security\.manage', 'billing\.manage', 'integrations\.manage'\]\]/
	);
	assert.match(protectedLayout, /\['\/onboarding', \['rentals\.manage'\]\]/);
	assert.match(protectedLayout, /\['\/audit', \['reports\.read'\]\]/);
	assert.match(
		protectedLayout,
		/\['\/ai', \['rentals\.read', 'work\.read', 'leasing\.terms\.read'\]\]/
	);
});
