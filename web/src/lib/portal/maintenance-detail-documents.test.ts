import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

describe('portal maintenance detail documents', () => {
	it('shows the selected tenant work order photos and documents in the detail dialog', () => {
		const source = readFileSync(
			new URL('../../routes/(portal)/portal/maintenance/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(source, /DocumentsPanel/);
		assert.match(source, /title="Photos & documents"/);
		assert.match(source, /entityType="WorkOrder"/);
		assert.match(source, /entityId=\{detail\.id\}/);
	});
});
