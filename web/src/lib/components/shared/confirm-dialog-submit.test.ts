import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync(new URL('./ConfirmDialog.svelte', import.meta.url), 'utf8');

describe('confirm dialog submit guard', () => {
	it('uses a local latch so rapid repeat clicks cannot submit the same destructive action twice', () => {
		assert.match(source, /let confirming = \$state\(false\)/);
		assert.match(source, /function handleConfirm\(\)/);
		assert.match(source, /if \(busy \|\| confirming\) return/);
		assert.match(source, /onclick=\{handleConfirm\}/);
		assert.match(source, /disabled=\{busy \|\| confirming\}/);
	});
});
