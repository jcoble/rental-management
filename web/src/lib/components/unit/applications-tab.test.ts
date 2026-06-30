import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync('src/lib/components/unit/tabs/ApplicationsTab.svelte', 'utf8');

describe('unit applications tab', () => {
	it('uses the empty-state application link action without also rendering the list toolbar action', () => {
		assert.match(source, /data-testid="unit-application-empty-create-link"/);
		assert.match(
			source,
			/\{#if !applicationsQuery\.isLoading && appList\.length > 0\}[\s\S]*data-testid="unit-application-create-link"[\s\S]*\{\/if\}[\s\S]*\{#if applicationsQuery\.isLoading\}/
		);
	});
});
