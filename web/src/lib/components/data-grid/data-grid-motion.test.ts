import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8');
const dataGrid = source('./DataGrid.svelte');
const styles = source('../../styles/m3-base.css');

describe('DataGrid motion', () => {
	it('reveals rows nonblockingly', () => {
		assert.equal(dataGrid.match(/m3-motion-reveal-list/g)?.length, 2);
		assert.match(styles, /\.m3-motion-reveal-list > \*\s*\{[\s\S]*?animation:\s*m3-motion-enter-quick/);
		assert.doesNotMatch(styles, /\.m3-motion-reveal-list > \*\s*\{[^}]*pointer-events:\s*none/);
	});

	it('disables reveal for reduced motion', () => {
		assert.match(styles, /@media \(prefers-reduced-motion: reduce\)[\s\S]*?\.m3-motion-reveal-list > \*[\s\S]*?animation:\s*none;/);
	});
});
