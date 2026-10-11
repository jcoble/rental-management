import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { readFileSync } from 'node:fs';

const endpointSource = readFileSync(
	new URL('../api/endpoints/tenants.ts', import.meta.url),
	'utf8'
);
const onboardingSource = readFileSync(
	new URL('../../routes/(protected)/onboarding/+page.svelte', import.meta.url),
	'utf8'
);

describe('guided tenant setup contract', () => {
	it('submits every reviewed tenant through one idempotent batch request', () => {
		assert.match(endpointSource, /api\.post<Tenant\[\]>\([\s\S]*'\/tenants\/guided-setup'/);
		assert.match(onboardingSource, /tenants\.createGuidedSetupBatch\(rows\)/);
		assert.doesNotMatch(onboardingSource, /for \(const t of rows\)/);
		assert.doesNotMatch(onboardingSource, /made\.push\(await tenants\.create/);
	});
});
