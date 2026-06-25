import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const pageSource = readFileSync(
	new URL('../../routes/(protected)/maintenance/recurring/+page.svelte', import.meta.url),
	'utf8'
);

describe('recurring maintenance accessibility', () => {
	it('gives row icon actions task-specific accessible names', () => {
		assert.match(
			pageSource,
			/aria-label=\{t\.isActive \? `Pause recurring task \$\{t\.title\}` : `Resume recurring task \$\{t\.title\}`\}/
		);
		assert.match(pageSource, /aria-label=\{`Edit recurring task \$\{t\.title\}`\}/);
		assert.match(pageSource, /aria-label=\{`Delete recurring task \$\{t\.title\}`\}/);
	});
});
