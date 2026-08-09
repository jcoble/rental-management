import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const source = readFileSync(new URL('./SimClockPanel.svelte', import.meta.url), 'utf8');

test('simulated-clock panel stays in a compact top-right position', () => {
	assert.match(source, /\.sim-panel\s*\{[\s\S]*?top:\s*calc\(3\.5rem \+ 12px\);[\s\S]*?right:\s*12px;/);
	assert.doesNotMatch(source, /\.sim-panel\s*\{[\s\S]*?(?:\n|\s)(?:left|bottom):\s*[^;]+;/);
	assert.doesNotMatch(source, /pointer-events:\s*none/);
});

test('expanded simulated-clock controls collapse their hitbox when inactive', () => {
	assert.match(
		source,
		/\.sim-panel:not\(\.sim-collapsed\):not\(:hover\):not\(:focus-within\)\s+\.sim-body\s*\{[\s\S]*?display:\s*none;/
	);
	assert.doesNotMatch(source, /\.sim-panel:not\(\.sim-collapsed\)[^{]*\{[\s\S]*?pointer-events:\s*none/);
});
