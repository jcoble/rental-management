import assert from 'node:assert/strict';
import { test } from 'node:test';

import { defaultSignerOrder, needsSigningOrderControls } from './signer-defaults.ts';

test('defaultSignerOrder puts the tenants first and the landlord side last', () => {
	const ordered = defaultSignerOrder([
		{ signerRole: 'Owner', nameSnapshot: 'Dana Landlord', signingOrder: 1 },
		{ signerRole: 'PrimaryTenant', nameSnapshot: 'Sam Renter', signingOrder: 2 }
	]);

	assert.deepEqual(
		ordered.map((signer) => [signer.nameSnapshot, signer.signingOrder]),
		[
			['Sam Renter', 1],
			['Dana Landlord', 2]
		]
	);
});

test('defaultSignerOrder numbers everyone 1..n and keeps the tenants in their given order', () => {
	const ordered = defaultSignerOrder([
		{ signerRole: 'Manager', nameSnapshot: 'Pat Manager', signingOrder: 9 },
		{ signerRole: 'PrimaryTenant', nameSnapshot: 'Sam Renter', signingOrder: 4 },
		{ signerRole: 'CoTenant', nameSnapshot: 'Jo Renter', signingOrder: 7 },
		{ signerRole: 'Guarantor', nameSnapshot: 'Alex Guarantor', signingOrder: 2 }
	]);

	assert.deepEqual(
		ordered.map((signer) => [signer.nameSnapshot, signer.signingOrder]),
		[
			['Sam Renter', 1],
			['Jo Renter', 2],
			['Alex Guarantor', 3],
			['Pat Manager', 4]
		]
	);
});

test('defaultSignerOrder does not change the signers it was given', () => {
	const signers = [{ signerRole: 'Owner', signingOrder: 5 }];
	defaultSignerOrder(signers);
	assert.equal(signers[0].signingOrder, 5);
});

test('needsSigningOrderControls is false for one tenant plus the landlord', () => {
	assert.equal(
		needsSigningOrderControls([{ signerRole: 'PrimaryTenant' }, { signerRole: 'Owner' }]),
		false
	);
});

test('needsSigningOrderControls is true once a second tenant has to sign', () => {
	assert.equal(
		needsSigningOrderControls([
			{ signerRole: 'PrimaryTenant' },
			{ signerRole: 'CoTenant' },
			{ signerRole: 'Owner' }
		]),
		true
	);
});
