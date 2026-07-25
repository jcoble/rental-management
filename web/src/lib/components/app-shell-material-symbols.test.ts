import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';

const appShell = readFileSync(new URL('./AppShell.svelte', import.meta.url), 'utf8');
const symbolStyles = readFileSync(
	new URL('../styles/material-symbols.css', import.meta.url),
	'utf8'
);

function materialSymbolNames(source: string) {
	const declarationStart = source.indexOf('const navGlyphByHref');
	const declarationEnd = source.indexOf('// Identity is sourced', declarationStart);
	const declaration = source.slice(declarationStart, declarationEnd);
	return [...declaration.matchAll(/:\s*'([a-z_]+)'/g)].map((match) => match[1]);
}

describe('AppShell Material Symbols subset', () => {
	test('uses only glyphs included in the self-hosted font subset', () => {
		const subsetComment = symbolStyles.slice(0, symbolStyles.indexOf('\n@font-face'));
		const missing = materialSymbolNames(appShell).filter(
			(glyph) => !new RegExp(`(?:^|[\\s,"])${glyph}(?:$|[\\s,"])`).test(subsetComment)
		);

		assert.deepEqual(missing, []);
	});

	test('maps known role destinations to available glyphs', () => {
		assert.match(
			appShell,
			/'\/settings\/notifications\/team-routing': 'group'/
		);
		assert.match(appShell, /'\/my-schedule': 'event'/);
		assert.doesNotMatch(appShell, /'\/profile':\s*'person'/);
	});
});
