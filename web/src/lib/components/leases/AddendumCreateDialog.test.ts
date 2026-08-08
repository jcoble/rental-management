import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync(new URL('./AddendumCreateDialog.svelte', import.meta.url), 'utf8');

describe('addendum create dialog', () => {
	it('renders an empty-template state with the real lease-template management link', () => {
		assert.match(
			source,
			/\{#if !templatesQuery\.isLoading && !templatesQuery\.isError && templatesQuery\.data\?\.items\.length === 0\}[\s\S]*data-testid="addendum-create-empty-templates"[\s\S]*No addendum templates exist yet\.[\s\S]*href="\/lease-templates"[\s\S]*\{\/if\}/
		);
	});

	it('uses addendum-specific validation copy when no template is selected', () => {
		assert.match(source, /Choose an active addendum template\./);
		assert.doesNotMatch(source, /Choose an active lease template\./);
	});
});
