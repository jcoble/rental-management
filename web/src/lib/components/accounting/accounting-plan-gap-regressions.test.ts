import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const read = (path: string) => readFile(new URL(path, import.meta.url), 'utf8');

test('accounting impact card renders journal line impacts before drawer opens', async () => {
	const source = await read('./AccountingImpactCard.svelte');
	assert.match(source, /journal\.lines/);
	assert.match(source, /formatSimpleJournalLineLabel/);
});

test('journal references link documents and both reversal directions', async () => {
	const source = await read('./JournalDetailDrawer.svelte');
	assert.match(source, /href=\{documentHref\(documentId\)\}/);
	assert.match(source, /detail\.reversalPublicIds as reversalPublicId/);
	assert.match(source, /journalPublicId = reversalPublicId/);
});

test('owner, banking, deposit, and reports surfaces expose accounting drill-ins', async () => {
	const [owners, banking, deposits, money] = await Promise.all([
		read('../../../routes/(protected)/owners-report/+page.svelte'),
		read('../../../routes/(protected)/banking/+page.svelte'),
		read('../../../../../mobile/lib/features/deposits/deposits_screen.dart'),
		read('../../../routes/(protected)/accounting/+page.svelte')
	]);
	assert.match(owners, /sourceType="OwnerDistribution"/);
	assert.match(banking, /AccountingImpactCard/);
	assert.match(deposits, /JournalSourceType\.securityDepositReceipt/);
	assert.match(money, /ReportsCatalog/);
});
