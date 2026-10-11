import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8');
const dataGrid = source('./DataGrid.svelte');
const styles = source('../../styles/m3-base.css');

describe('DataGrid accessibility', () => {
	it('announces keyboard sort', () => {
		assert.match(dataGrid, /<button[\s\S]*?class="datagrid-sort-control"[\s\S]*?aria-label=\{`Sort by \$\{col\.title\}`\}[\s\S]*?onclick=\{\(\) => toggleSort\(col\)\}/);
		assert.match(dataGrid, /aria-sort=\{col\.sortable[\s\S]*?'ascending'[\s\S]*?'descending'[\s\S]*?'none'/);
		assert.match(dataGrid, /sortDir === 'asc' \? 'desc' : sortDir === 'desc' \? 'none' : 'asc'/);
	});

	it('keeps nested actions independent', () => {
		assert.match(dataGrid, /function stopRowNavigation\(event: MouseEvent \| KeyboardEvent\)/);
		assert.match(dataGrid, /col\.isAction[\s\S]*?onclick=\{col\.isAction \? stopRowNavigation : undefined\}/);
		assert.match(dataGrid, /data-testid="datagrid-mobile-actions"[\s\S]*?onclick=\{stopRowNavigation\}/);
	});

	it('uses 44px targets', () => {
		assert.match(styles, /\.datagrid-sort-control\s*\{[\s\S]*?min-height:\s*44px;[\s\S]*?min-width:\s*44px;/);
	});
});
