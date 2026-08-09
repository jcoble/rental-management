import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const componentSource = readFileSync(new URL('./LeaseManagementDetail.svelte', import.meta.url), 'utf8');
const typesSource = readFileSync(new URL('../../types/index.ts', import.meta.url), 'utf8');

describe('agreement signing-progress availability', () => {
	it('gates the signing button and progress panel on native signature-request presence', () => {
		assert.match(componentSource, /issuedArtifact && agreement\.hasSignatureRequest/);
		assert.match(componentSource, /signatureProgressAgreementId === agreement\.leaseAgreementId[\s\S]*agreement\.hasSignatureRequest/);
	});

	it('explains signed-outside-esign agreements according to execution status', () => {
		assert.match(componentSource, /issuedArtifact && !agreement\.hasSignatureRequest/);
		assert.match(
			componentSource,
			/{#if agreement\.fullyExecutedAtUtc}[\s\S]*Signed outside e-sign — no signing links to track\.[\s\S]*{:else}[\s\S]*Signing handled outside e-sign — mark as signed when complete\.[\s\S]*{\/if}/
		);
	});

	it('mirrors signature request presence and identity in the agreement history type', () => {
		assert.match(typesSource, /signatureRequestId\?: number \| null/);
		assert.match(typesSource, /hasSignatureRequest: boolean/);
	});
});
