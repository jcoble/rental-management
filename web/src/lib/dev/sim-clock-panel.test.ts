import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const source = readFileSync(new URL('./SimClockPanel.svelte', import.meta.url), 'utf8');

test('simulated-clock panel is docked in the top-bar slot', () => {
	assert.match(source, /\.sim-panel\s*\{[\s\S]*?position:\s*absolute;/);
	assert.match(source, /\.sim-panel\s*\{[\s\S]*?top:\s*50%;[\s\S]*?right:\s*0;/);
	assert.doesNotMatch(source, /bottom:\s*12px/);
	assert.doesNotMatch(source, /pointer-events:\s*none/);
});

test('expanded simulated-clock controls collapse their hitbox when inactive', () => {
	assert.match(
		source,
		/\.sim-panel:not\(\.sim-collapsed\):not\(:hover\):not\(:focus-within\)\s+\.sim-body\s*\{[\s\S]*?display:\s*none;/
	);
	assert.doesNotMatch(source, /\.sim-panel:not\(\.sim-collapsed\)[^{]*\{[\s\S]*?pointer-events:\s*none/);
});
