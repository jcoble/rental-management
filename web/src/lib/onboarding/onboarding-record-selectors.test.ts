import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

function onboardingPageSource(): string {
	return readFileSync(
		new URL('../../routes/(protected)/onboarding/+page.svelte', import.meta.url),
		'utf8'
	);
}

describe('onboarding record selectors', () => {
	it('renders the owner record selector above the owner fields', () => {
		const source = onboardingPageSource();
		const selector = source.indexOf('testid="onboarding-owner-record-select"');
		const nameField = source.indexOf('data-testid="onboarding-owner-name"');

		assert.ok(selector > -1, 'owner record selector should be rendered');
		assert.ok(nameField > -1, 'owner name field should be rendered');
		assert.ok(selector < nameField, 'owner selector should appear before editable owner fields');
		assert.match(source, /onboardingOwnerRecordOptions\(/);
	});

	it('renders the property record selector above the property fields', () => {
		const source = onboardingPageSource();
		const selector = source.indexOf('testid="onboarding-property-record-select"');
		const nameField = source.indexOf('data-testid="onboarding-property-name"');

		assert.ok(selector > -1, 'property record selector should be rendered');
		assert.ok(nameField > -1, 'property name field should be rendered');
		assert.ok(selector < nameField, 'property selector should appear before editable property fields');
		assert.match(source, /onboardingPropertyRecordOptions\(/);
	});
});
