import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { resolveInitialPortfolioId } from './portfolio-selection.ts';

describe('resolveInitialPortfolioId', () => {
	it('uses the authenticated portfolio over a stale stored id from another account', () => {
		assert.deepEqual(resolveInitialPortfolioId('1', 3), { id: 3, shouldPersist: true });
	});

	it('seeds a missing stored id from the authenticated portfolio', () => {
		assert.deepEqual(resolveInitialPortfolioId(null, 3), { id: 3, shouldPersist: true });
	});

	it('keeps a matching stored id without rewriting storage', () => {
		assert.deepEqual(resolveInitialPortfolioId('3', 3), { id: 3, shouldPersist: false });
	});

	it('uses a stored id only when there is no authenticated fallback', () => {
		assert.deepEqual(resolveInitialPortfolioId('2', undefined), { id: 2, shouldPersist: false });
	});

	it('ignores invalid storage without an authenticated fallback', () => {
		assert.deepEqual(resolveInitialPortfolioId('not-a-number', undefined), { id: null, shouldPersist: false });
	});
});
