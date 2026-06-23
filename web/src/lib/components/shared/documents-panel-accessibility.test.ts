import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync(new URL('./DocumentsPanel.svelte', import.meta.url), 'utf8');

describe('documents panel accessibility', () => {
	it('gives each icon-only delete button a document-specific accessible name', () => {
		assert.match(source, /aria-label=\{`Delete \$\{doc\.fileName\}`\}/);
	});
});
