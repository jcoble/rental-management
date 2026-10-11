import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';

const loadingState = readFileSync(new URL('./LoadingState.svelte', import.meta.url), 'utf8');

describe('shared loading state contract', () => {
	test('announces progress without exposing a plain-text-only primary loader', () => {
		assert.match(loadingState, /role="status"/);
		assert.match(loadingState, /aria-live="polite"/);
		assert.match(loadingState, /aria-busy="true"/);
		assert.match(loadingState, /<span class="sr-only">\{label\}<\/span>/);
		assert.match(loadingState, /variant === 'page'/);
		assert.match(loadingState, /variant === 'section'/);
	});

	test('keeps skeleton geometry stable and respects reduced motion', () => {
		assert.match(loadingState, /\.loading-page\s*\{[\s\S]*min-height:/);
		assert.match(loadingState, /\.loading-section\s*\{[\s\S]*min-height:/);
		assert.match(loadingState, /@media \(prefers-reduced-motion: reduce\)/);
		assert.match(loadingState, /\.loading-pulse,[\s\S]*\.loading-spinner-ring[\s\S]*animation: none/);
	});
});
