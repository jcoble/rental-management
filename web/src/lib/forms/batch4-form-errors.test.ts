import assert from 'node:assert/strict';
import { test } from 'node:test';
import { validationErrorsToFormErrors } from './form-errors.ts';

test('S26-BUG-2 maps ASP.NET field names back to the form fields', () => {
	assert.deepEqual(
		validationErrorsToFormErrors({
			FirstName: ['First name is too long.'],
			'$.LastName': ['Last name is required.'],
			Email: ['Enter a valid email.']
		}),
		{
			firstName: 'First name is too long.',
			lastName: 'Last name is required.',
			email: 'Enter a valid email.'
		}
	);
});

test('S26-POT-4 keeps the first server message for each mapped field', () => {
	assert.deepEqual(
		validationErrorsToFormErrors({ Title: ['Title is too long.', 'Another message'] }),
		{ title: 'Title is too long.' }
	);
});
