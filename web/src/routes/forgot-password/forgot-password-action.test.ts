import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const source = readFileSync(new URL('./+page.server.ts', import.meta.url), 'utf8');

test('forgot-password action keeps a retryable state for failed API transport', () => {
	assert.match(source, /if \(!response\.ok\)/);
	assert.match(source, /return fail\(503/);
	assert.match(source, /sent: false/);
	assert.match(source, /email\s*\}/);
});
