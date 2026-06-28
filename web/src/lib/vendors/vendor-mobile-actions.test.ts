import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8');

describe('vendor mobile actions', () => {
	it('wires vendor edit and delete controls into the mobile card list', () => {
		const vendorsPage = source('../../routes/(protected)/vendors/+page.svelte');
		const dataGrid = source('../components/data-grid/DataGrid.svelte');

		assert.match(dataGrid, /mobileActions\?: Snippet<\[T\]>/);
		assert.match(dataGrid, /data-testid="datagrid-mobile-actions"/);
		assert.match(vendorsPage, /mobileActions=\{vendorActionsCell\}/);
		assert.match(vendorsPage, /data-testid="vendor-edit"/);
		assert.match(vendorsPage, /data-testid="vendor-delete"/);
	});
});
