import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { ensureSelectedOption } from './lease-edit-options.ts';

describe('ensureSelectedOption', () => {
	it('adds the current lease association after the placeholder when it is missing', () => {
		const options = [
			{ value: '', label: 'Select property' },
			{ value: '2', label: 'Elm Street' },
		];

		assert.deepEqual(ensureSelectedOption(options, '1', 'Oak House'), [
			{ value: '', label: 'Select property' },
			{ value: '1', label: 'Oak House' },
			{ value: '2', label: 'Elm Street' },
		]);
	});

	it('keeps an existing option when the current association is already loaded', () => {
		const options = [
			{ value: '', label: 'Select tenant' },
			{ value: '7', label: 'Jordan Lee' },
		];

		assert.equal(ensureSelectedOption(options, '7', 'Jordan Lee'), options);
	});

	it('ignores blank selected values', () => {
		const options = [{ value: '', label: 'Select unit' }];

		assert.equal(ensureSelectedOption(options, '', 'Unit 1'), options);
	});
});
