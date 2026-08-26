import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

describe('dashboard work order display', () => {
	it('does not render raw status or priority enum values from the dashboard page', () => {
		const source = readFileSync(new URL('../../routes/(protected)/+page.svelte', import.meta.url), 'utf8');

		assert.doesNotMatch(source, /\{order\.status\}/);
		assert.doesNotMatch(source, /\{order\.priority\}/);
		assert.doesNotMatch(source, /\{bullet\.severity\}/);
	});
});
