import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const endpointSource = readFileSync(
	new URL('../api/endpoints/lease-managements.ts', import.meta.url),
	'utf8'
);
const draftDialogSource = readFileSync(
	new URL('../components/leases/AgreementDraftDialog.svelte', import.meta.url),
	'utf8'
);
const successorDialogSource = readFileSync(
	new URL('../components/leases/AgreementSuccessorDialog.svelte', import.meta.url),
	'utf8'
);
const detailPageSource = readFileSync(
	new URL('../../routes/(protected)/leases/[id]/+page.svelte', import.meta.url),
	'utf8'
);
const possessionActionsSource = readFileSync(
	new URL('../components/leases/PossessionActions.svelte', import.meta.url),
	'utf8'
);

describe('canonical lease lifecycle action hub', () => {
	it('uses typed canonical draft, successor, preparation, and issuance endpoints', () => {
		assert.match(endpointSource, /getAgreementDraft:/);
		assert.match(endpointSource, /editAgreementDraft:/);
		assert.match(endpointSource, /createAgreementSuccessorDraft:/);
		assert.match(endpointSource, /prepareAgreementIssuance:/);
		assert.match(endpointSource, /issueAgreement:/);
		assert.match(endpointSource, /draftRevision: number/);
		assert.match(endpointSource, /\/successor-drafts`/);
		assert.match(endpointSource, /\/issuance-preparations`/);
		assert.match(endpointSource, /headers: \{ "Idempotency-Key": operationKey \}/);
	});

	it('edits and issues the exact canonical draft revision', () => {
		assert.match(draftDialogSource, /leaseManagements\.getAgreementDraft/);
		assert.match(draftDialogSource, /draftRevision: draft\.draftRevision/);
		assert.match(draftDialogSource, /termsSchemaVersion: draft\.termsSchemaVersion/);
		assert.match(draftDialogSource, /termsPayload: draft\.termsPayload/);
		assert.match(draftDialogSource, /leaseManagements\.prepareAgreementIssuance/);
		assert.match(draftDialogSource, /\{ \.\.\.prepared, subject: subject\.trim\(\) \}/);
		assert.match(draftDialogSource, /Save the current draft changes before issuing/);
		assert.match(draftDialogSource, /isRequired: true/);
		assert.match(draftDialogSource, /draft\.signers\.some\(\(signer\) => !signer\.isRequired\)/);
		assert.doesNotMatch(draftDialogSource, /bind:checked=\{signer\.isRequired\}/);
	});

	it('creates each supported successor and opens the returned draft', () => {
		for (const changeType of ['Correction', 'Restatement', 'Renewal', 'MonthToMonth']) {
			assert.match(detailPageSource, new RegExp(`changeType: '${changeType}'`));
		}
		assert.match(successorDialogSource, /leaseManagements\.createAgreementSuccessorDraft/);
		assert.match(successorDialogSource, /leaseManagements\.getEffectiveAddendumSeries/);
		assert.match(successorDialogSource, /sourceAddendumSeriesPublicId: series\.seriesPublicId/);
		assert.doesNotMatch(successorDialogSource, /addendumDecisionBlocked/);
		assert.match(detailPageSource, /editAgreementId = result\.leaseAgreementId/);
	});

	it('pages agreement history on the server and keeps artifacts distinct', () => {
		assert.match(detailPageSource, /skip: agreementSkip/);
		assert.match(detailPageSource, /take: AGREEMENT_PAGE_SIZE/);
		assert.match(detailPageSource, /<Pagination bind:skip=\{agreementSkip\}/);
		assert.doesNotMatch(detailPageSource, /take:\s*100/);
		assert.match(detailPageSource, /> Issued PDF</);
		assert.match(detailPageSource, /> Executed PDF</);
		assert.match(detailPageSource, /agreement\.hasSourceScan/);
		assert.match(detailPageSource, /href=\{`\/units\/\$\{summary\.unitId\}\?tab=lease`\}/);
	});

	it('gives and returns possession through canonical idempotent commands', () => {
		assert.match(endpointSource, /givePossession:/);
		assert.match(endpointSource, /returnPossession:/);
		assert.match(endpointSource, /\/give-possession`/);
		assert.match(endpointSource, /\/return-possession`/);
		assert.match(endpointSource, /getReturnPossessionContext:/);
		assert.match(possessionActionsSource, /leaseManagements\.givePossession/);
		assert.match(possessionActionsSource, /leaseManagements\.getReturnPossessionContext/);
		assert.match(possessionActionsSource, /leaseManagements\.returnPossession/);
		assert.match(possessionActionsSource, /returnOperation\.fingerprint !== fingerprint/);
		assert.match(possessionActionsSource, /returnContext\.parties/);
		assert.match(possessionActionsSource, /returnContext\.activeTenantUserAccesses/);
		assert.doesNotMatch(possessionActionsSource, /summary\.parties/);
		assert.match(detailPageSource, /<PossessionActions \{summary\} onchanged=\{refreshLease\} \/>/);
	});
});
