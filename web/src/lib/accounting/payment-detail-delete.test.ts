import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

// TSK-457: the delete/ConfirmDialog flow moved out of the thin [id] route wrapper into the
// extracted PaymentDetail record component (folded into the Rent tab on ?payment= select).
const source = readFileSync(
	new URL('../components/records/PaymentDetail.svelte', import.meta.url),
	'utf8'
);

describe('payment detail delete flow', () => {
	it('opens a confirmation dialog instead of deleting immediately from the header button', () => {
		assert.match(source, /import ConfirmDialog from '\$lib\/components\/shared\/ConfirmDialog\.svelte';/);
		assert.doesNotMatch(source, /onclick=\{\(\) => deleteMutation\.mutate\(\)\}/);
		assert.match(source, /open=\{deleteTarget !== null\}/);
		assert.match(source, /title="Delete payment"/);
		assert.match(source, /onconfirm=\{\(\) => .*deleteMutation\.mutate\(\)\}/s);
	});
});
