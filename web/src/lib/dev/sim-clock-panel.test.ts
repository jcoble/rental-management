import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const source = readFileSync(new URL('./SimClockPanel.svelte', import.meta.url), 'utf8');

test('desktop simulated-clock panel stays outside the sidebar hit area', () => {
	assert.match(source, /\.sim-panel\s*\{[\s\S]*?left:\s*12px;/);
	assert.match(
		source,
		/@media\s*\(min-width:\s*768px\)\s*\{[\s\S]*?\.sim-panel\s*\{[\s\S]*?left:\s*calc\(15rem \+ 12px\);/
	);
	assert.doesNotMatch(source, /pointer-events:\s*none/);
});

test('expanded simulated-clock controls collapse their hitbox when inactive', () => {
	assert.match(
		source,
		/\.sim-panel:not\(\.sim-collapsed\):not\(:hover\):not\(:focus-within\)\s+\.sim-body\s*\{[\s\S]*?display:\s*none;/
	);
	assert.doesNotMatch(source, /\.sim-panel:not\(\.sim-collapsed\)[^{]*\{[\s\S]*?pointer-events:\s*none/);
});
