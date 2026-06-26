import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { clearFieldError } from './form-errors.ts';

describe('clearFieldError', () => {
	it('removes only the corrected field error', () => {
		assert.deepEqual(clearFieldError({ name: 'Required', email: 'Invalid' }, 'name'), {
			email: 'Invalid',
		});
	});

	it('returns the same object when the field has no error', () => {
		const errors = { name: 'Required' };

		assert.equal(clearFieldError(errors, 'email'), errors);
	});
});
