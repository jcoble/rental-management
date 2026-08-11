import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import { cancelIdempotentMutation, idempotentMutation } from './idempotency.ts';

const endpointSources = [
	'./endpoints/expenses.ts',
	'./endpoints/recurring-expenses.ts',
	'./endpoints/loans.ts',
	'./endpoints/owner-distributions.ts',
	'./endpoints/owners.ts'
].map((path) => [path, readFileSync(new URL(path, import.meta.url), 'utf8')] as const);

describe('idempotentMutation', () => {
	it('retains one operation key after an ambiguous failure and clears it after success', async () => {
		const scope = `idempotency-test:retry:${crypto.randomUUID()}`;
		let firstKey = '';

		await assert.rejects(
			idempotentMutation(scope, async (key) => {
				firstKey = key;
				throw new Error('connection closed before the response arrived');
			}),
			/connection closed/
		);

		let retryKey = '';
		await idempotentMutation(scope, async (key) => {
			retryKey = key;
			return 'confirmed';
		});
		assert.equal(retryKey, firstKey);

		let nextKey = '';
		await idempotentMutation(scope, async (key) => {
			nextKey = key;
			return 'next operation';
		});
		assert.notEqual(nextKey, firstKey);
	});

	it('abandons the retained key after explicit cancellation', async () => {
		const scope = `idempotency-test:cancel:${crypto.randomUUID()}`;
		let abandonedKey = '';

		await assert.rejects(
			idempotentMutation(scope, async (key) => {
				abandonedKey = key;
				throw new Error('ambiguous failure');
			})
		);
		cancelIdempotentMutation(scope);

		let replacementKey = '';
		await idempotentMutation(scope, async (key) => {
			replacementKey = key;
			return 'cancelled operation replaced';
		});
		assert.notEqual(replacementKey, abandonedKey);
	});

	it('releases the browser key only after the server returns a terminal reconciled attempt', async () => {
		const scope = `idempotency-test:terminal:${crypto.randomUUID()}`;
		let firstKey = '';
		await assert.rejects(
			idempotentMutation(scope, async (key) => {
				firstKey = key;
				throw {
					status: 409,
					extensions: {
					paymentAttemptId: 44,
					attemptState: 'Failed'
					}
				};
			})
		);

		let replacementKey = '';
		await idempotentMutation(scope, async (key) => {
			replacementKey = key;
			return 'new attempt';
		});
		assert.notEqual(replacementKey, firstKey);
	});

	it('sends the retained operation key as Idempotency-Key from every money endpoint', () => {
		for (const [path, source] of endpointSources) {
			assert.match(source, /idempotentMutation\(/, path);
			assert.match(source, /headers:\s*\{\s*["']Idempotency-Key["']:\s*key\s*\}/, path);
		}
	});
});
