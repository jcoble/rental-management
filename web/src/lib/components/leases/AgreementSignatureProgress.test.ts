import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const componentSource = readFileSync(
	new URL('./AgreementSignatureProgress.svelte', import.meta.url),
	'utf8'
);
const agreementApiSource = readFileSync(
	new URL('../../api/endpoints/lease-managements.ts', import.meta.url),
	'utf8'
);
const addendumApiSource = readFileSync(
	new URL('../../api/endpoints/lease-addendums.ts', import.meta.url),
	'utf8'
);

describe('agreement signature invitation resend contract', () => {
	it('offers a confirmed resend only for one unsigned signer on an eligible packet', () => {
		assert.match(componentSource, /Resend invitation/);
		assert.match(componentSource, /globalThis\.confirm/);
		assert.match(componentSource, /signer\.status !== 'Signed'/);
		assert.match(componentSource, /crypto\.randomUUID\(\)/);
		assert.match(componentSource, /leaseAgreementSignerId/);
	});

	it('makes failed delivery actionable and reports the result in plain language', () => {
		assert.match(componentSource, /data-testid="delivery-failed-resend-actions"/);
		assert.match(componentSource, /showSuccess\('Invitation queued again/);
		assert.match(componentSource, /showError\(apiErrorMessage/);
		assert.doesNotMatch(componentSource, /Retry or internal provider details are intentionally not shown here/);
	});

	it('uses idempotent signer-specific agreement and addendum endpoints', () => {
		assert.match(
			agreementApiSource,
			/agreements\/\$\{leaseAgreementId\}\/signers\/\$\{leaseAgreementSignerId\}\/resend-invitation/
		);
		assert.match(
			addendumApiSource,
			/addenda\/\$\{leaseAddendumId\}\/signers\/\$\{leaseAddendumSignerId\}\/resend-invitation/
		);
		for (const source of [agreementApiSource, addendumApiSource]) {
			assert.match(source, /idempotentJson<ResendNativeEsignInvitationResponse>/);
		}
	});
});
