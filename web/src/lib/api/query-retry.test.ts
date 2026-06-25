import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { shouldRetryQuery } from './query-retry.ts';

describe('query retry policy', () => {
	it('retries transient request timeout or abort errors', () => {
		assert.equal(shouldRetryQuery(0, { status: 408 }), true);
		assert.equal(shouldRetryQuery(1, { status: 408 }), true);
		assert.equal(shouldRetryQuery(2, { status: 408 }), false);
	});

	it('does not retry deterministic client errors', () => {
		assert.equal(shouldRetryQuery(0, { status: 400 }), false);
		assert.equal(shouldRetryQuery(0, { status: 404 }), false);
	});

	it('retries network and server errors twice', () => {
		assert.equal(shouldRetryQuery(0, new Error('Failed to fetch')), true);
		assert.equal(shouldRetryQuery(1, { status: 500 }), true);
		assert.equal(shouldRetryQuery(2, { status: 500 }), false);
	});
});
