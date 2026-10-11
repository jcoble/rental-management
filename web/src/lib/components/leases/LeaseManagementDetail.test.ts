import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import { outsideEsignLabel } from '../../leases/lease-signing-labels.ts';

const componentSource = readFileSync(new URL('./LeaseManagementDetail.svelte', import.meta.url), 'utf8');
const typesSource = readFileSync(new URL('../../types/index.ts', import.meta.url), 'utf8');

describe('agreement signing-progress availability', () => {
	it('gates the signing button and progress panel on native signature-request presence', () => {
		assert.match(componentSource, /issuedArtifact && agreement\.hasSignatureRequest/);
		assert.match(componentSource, /signatureProgressAgreementId === agreement\.leaseAgreementId[\s\S]*agreement\.hasSignatureRequest/);
	});

	it('explains signed-outside-esign agreements according to execution status', () => {
		assert.match(componentSource, /issuedArtifact && !agreement\.hasSignatureRequest/);
		assert.match(componentSource, /outsideEsignLabel\(agreement\.fullyExecutedAtUtc\)/);
		assert.equal(
			outsideEsignLabel('2027-01-15T12:00:00Z'),
			'Signed outside Rental Command — no signing links to track.'
		);
		assert.equal(
			outsideEsignLabel(null),
			'Signing handled outside Rental Command — mark as signed when complete.'
		);
	});

	it('mirrors signature request presence and identity in the agreement history type', () => {
		assert.match(typesSource, /signatureRequestId\?: number \| null/);
		assert.match(typesSource, /hasSignatureRequest: boolean/);
	});
});
