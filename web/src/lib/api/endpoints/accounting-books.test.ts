import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync(new URL('./accounting-books.ts', import.meta.url), 'utf8');

describe('accounting books API contract', () => {
	it('builds the server query names for chart of accounts and the allowlisted GL sorts', () => {
		assert.match(source, /buildChartOfAccountsPath[\s\S]*activeOnly: params\.activeOnly/);
		assert.match(source, /buildGeneralLedgerPath[\s\S]*accountId[\s\S]*effectiveFrom[\s\S]*effectiveTo/);
		assert.match(source, /export type GeneralLedgerSort =/);
		for (const sort of ['effectiveOn', '-effectiveOn', 'postedAtUtc', '-postedAtUtc', 'accountCode', '-accountCode']) {
			assert.match(source, new RegExp(`['"]${sort}['"]`));
		}
		assert.match(source, /sourceType: params\.sourceType/);
		assert.match(source, /sourceId: params\.sourceId/);
	});

	it('binds the read-model fields and date semantics without client-side accounting math', () => {
		for (const field of [
			'accountType',
			'normalBalance',
			'totalDebits',
			'totalCredits',
			'currentEarnings',
			'isBalanced',
			'documentIds'
		]) {
			assert.match(source, new RegExp(`\\b${field}\\b`));
		}
		assert.match(source, /buildTrialBalancePath[\s\S]*to: params\.to/);
		assert.doesNotMatch(source, /buildTrialBalancePath[\s\S]*from:/);
		assert.match(source, /import \{ idempotentMutation \} from '\.\.\/idempotency';/);
		assert.match(
			source,
			/idempotentMutation\(`accounting:chart-of-accounts:create:\$\{JSON\.stringify\(body\)\}`,[\s\S]*api\.post<ChartOfAccountsRow>\('\/accounting\/chart-of-accounts', body, \{[\s\S]*'Idempotency-Key': key/
		);
		assert.match(
			source,
			/idempotentMutation\(`accounting:chart-of-accounts:update:\$\{id\}:\$\{JSON\.stringify\(body\)\}`,[\s\S]*api\.patch<ChartOfAccountsRow>\(`\/accounting\/chart-of-accounts\/\$\{id\}`, body, \{[\s\S]*'Idempotency-Key': key/
		);
	});
});
