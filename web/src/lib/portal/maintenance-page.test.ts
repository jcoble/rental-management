import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

describe('portal maintenance page', () => {
	it('shows validation for empty tenant maintenance requests', () => {
		const source = readFileSync(
			new URL('../../routes/(portal)/portal/maintenance/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(source, /let requestSubmitted = \$state\(false\)/);
		assert.match(source, /requestTitleError/);
		assert.match(source, /requestDescriptionError/);
		assert.match(source, /data-testid="portal-request-title-error"/);
		assert.match(source, /data-testid="portal-request-description-error"/);
		assert.doesNotMatch(source, /if \(!form\.title\.trim\(\) \|\| !form\.description\.trim\(\)\) return;/);
	});

	it('uses stable request loaders and retry actions', () => {
		const source = readFileSync(
			new URL('../../routes/(portal)/portal/maintenance/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(source, /LoadingState/);
		assert.match(source, /portal-work-orders-loading/);
		assert.match(source, /portal-work-order-detail-loading/);
		assert.match(source, /workOrdersQuery\.refetch\(\)/);
		assert.match(source, /detailQuery\.refetch\(\)/);
	});
});
