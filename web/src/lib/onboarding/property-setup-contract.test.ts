import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { readFileSync } from 'node:fs';

const endpointSource = readFileSync(
	new URL('../api/endpoints/properties.ts', import.meta.url),
	'utf8'
);
const onboardingSource = readFileSync(
	new URL('../../routes/(protected)/onboarding/+page.svelte', import.meta.url),
	'utf8'
);

describe('guided property setup contract', () => {
	it('persists an explicit rental structure instead of inferring it from property type', () => {
		assert.match(onboardingSource, /value="SingleRental"/);
		assert.match(onboardingSource, /value="MultiRental"/);
		assert.doesNotMatch(onboardingSource, /SINGLE_UNIT_TYPES/);
	});

	it('saves the property and units through one idempotent setup command', () => {
		assert.match(endpointSource, /api\.post<SetupPropertyResponse>\(["']\/properties\/setup["']/);
		assert.match(onboardingSource, /properties\.setup\(\{/);
		assert.doesNotMatch(onboardingSource, /await properties\.create\(vars\.property\)/);
		assert.doesNotMatch(onboardingSource, /await properties\.createUnit\(property\.id/);
	});
});
