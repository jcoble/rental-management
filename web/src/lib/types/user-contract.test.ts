import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import type { RegisterRequest } from './user.ts';

const webSource = readFileSync(new URL('./user.ts', import.meta.url), 'utf8');
const apiSource = readFileSync(
	new URL('../../../../RentalCommand.Api/DTOs/AuthDtos.cs', import.meta.url),
	'utf8'
);

describe('registration request wire contract', () => {
	it('requires the consent member in the typed web payload', () => {
		const request: RegisterRequest = {
			email: 'landlord@example.test',
			password: 'password123',
			displayName: 'Landlord',
			termsPrivacyAccepted: true
		};

		assert.equal(request.termsPrivacyAccepted, true);
		assert.match(webSource, /termsPrivacyAccepted:\s*boolean/);
	});

	it('stays paired with the API RegisterRequest consent member', () => {
		assert.match(apiSource, /class RegisterRequest[\s\S]*TermsPrivacyAccepted/);
		assert.match(webSource, /interface RegisterRequest[\s\S]*termsPrivacyAccepted/);
	});
});
