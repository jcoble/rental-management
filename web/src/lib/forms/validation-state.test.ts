import assert from 'node:assert/strict';
import { test } from 'node:test';
import { isPositiveNumericInput } from './validation-state.ts';

test('S26-BUG-3 only clears an amount error for a finite positive value', () => {
	assert.equal(isPositiveNumericInput(''), false);
	assert.equal(isPositiveNumericInput(0), false);
	assert.equal(isPositiveNumericInput('-1'), false);
	assert.equal(isPositiveNumericInput('0.01'), true);
	assert.equal(isPositiveNumericInput('12'), true);
});
