import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const pageSource = readFileSync(
	new URL('../../routes/apply/[token]/+page.svelte', import.meta.url),
	'utf8'
);

test('public application autofill clears validation errors for fields it filled', () => {
	assert.match(pageSource, /function clearFieldError\(name: string\)/);
	assert.match(pageSource, /f\.firstName\?\.value[\s\S]*clearFieldError\('firstName'\)/);
	assert.match(pageSource, /f\.lastName\?\.value[\s\S]*clearFieldError\('lastName'\)/);
});

test('public application manual edits clear stale field and consent errors', () => {
	assert.match(pageSource, /oninput=\{\(e\) => \{[\s\S]*clearFieldError\(name\);[\s\S]*\}\}/);
	assert.match(pageSource, /onchange=\{\(\) => clearFieldError\('consent'\)\}/);
});
