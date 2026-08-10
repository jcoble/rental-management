import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const page = readFileSync(new URL('./+page.svelte', import.meta.url), 'utf8');
const action = readFileSync(new URL('./+page.server.ts', import.meta.url), 'utf8');

describe('registration terms and privacy consent', () => {
	it('renders one required linked consent checkbox on registration only', () => {
		assert.match(page, /name="termsPrivacyAccepted"/);
		assert.match(page, /type="checkbox"/);
		assert.match(page, /required/);
		assert.match(page, /href="\/terms"/);
		assert.match(page, /href="\/privacy"/);
		assert.match(page, /I agree to the/);
	});

	it('rejects a missing checkbox before calling the API and sends consent when checked', () => {
		assert.match(action, /formData\.get\('termsPrivacyAccepted'\) === 'on'/);
		assert.match(action, /You must agree to the Terms of Service and Privacy Policy/);
		assert.match(action, /termsPrivacyAccepted\s*\}/);
	});
});
