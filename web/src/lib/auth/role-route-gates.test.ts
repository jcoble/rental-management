import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const protectedLayout = readFileSync(
	new URL('../../routes/(protected)/+layout.server.ts', import.meta.url),
	'utf8'
);
const appShell = readFileSync(new URL('../components/AppShell.svelte', import.meta.url), 'utf8');
const experiencePolicy = readFileSync(new URL('./experience-policy.ts', import.meta.url), 'utf8');

test('direct staff routes use the same role-capability gates as navigation', () => {
	assert.match(
		protectedLayout,
		/import \{ canAccessRoute, CAPABILITY \} from '\$lib\/auth\/experience-policy'/
	);
	assert.match(protectedLayout, /canAccessRoute\(url\.pathname, activeExperience, effectiveCapabilities\)/);
	assert.match(
		appShell,
		/import \{ canAccessRoute, CAPABILITY, safeLandingForAccess \} from '\$lib\/auth\/experience-policy'/
	);
	assert.match(appShell, /canAccessRoute\(item\.href, activeExperience, activeCapabilities\)/);
	assert.match(experiencePolicy, /prefix: '\/admin\/users'/);
	assert.match(experiencePolicy, /prefix: '\/settings'/);
	assert.match(experiencePolicy, /prefix: '\/settings\/notifications\/my-alerts'/);
	assert.match(experiencePolicy, /prefix: '\/settings\/notifications\/team-routing'/);
	assert.match(experiencePolicy, /prefix: '\/onboarding'/);
	assert.match(experiencePolicy, /prefix: '\/audit'/);
	assert.match(experiencePolicy, /prefix: '\/ai'/);
});
