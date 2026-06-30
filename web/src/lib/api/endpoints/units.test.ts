import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { normalizeOptionalApiResult } from '../optional-result.ts';

describe('unit listing endpoint helpers', () => {
	it('normalizes empty optional API responses to null for query data', () => {
		assert.equal(normalizeOptionalApiResult(undefined), null);
		assert.equal(normalizeOptionalApiResult(null), null);
		assert.deepEqual(normalizeOptionalApiResult({ id: 10 }), { id: 10 });
	});
});
