import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const endpointSource = readFileSync(new URL('../api/endpoints/lease-managements.ts', import.meta.url), 'utf8');
const draftDialogSource = readFileSync(
	new URL('../components/leases/AgreementDraftDialog.svelte', import.meta.url),
	'utf8',
);
const successorDialogSource = readFileSync(
	new URL('../components/leases/AgreementSuccessorDialog.svelte', import.meta.url),
	'utf8',
);
const detailPageSource = readFileSync(
	new URL('../../routes/(protected)/leases/[id]/+page.svelte', import.meta.url),
	'utf8',
);
const possessionActionsSource = readFileSync(
	new URL('../components/leases/PossessionActions.svelte', import.meta.url),
	'utf8',
);
const endingDispositionSource = readFileSync(
	new URL('../components/leases/EndingDispositionDialog.svelte', import.meta.url),
	'utf8',
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

	it('supports the correction successor UX and abandoned-draft cancellation', () => {
		assert.match(endpointSource, /correctionReason: string \| null/);
		assert.match(endpointSource, /cancelAgreementSuccessorDraft:/);
		assert.match(endpointSource, /\/cancel-draft`/);
		assert.match(successorDialogSource, /agreement-successor-correction-reason/);
		assert.match(
			successorDialogSource,
			/current signed lease stays in effect until the replacement is fully signed/i,
		);
		assert.match(draftDialogSource, /agreement-correction-comparison/);
		assert.match(draftDialogSource, /agreement-correction-changed-field/);
		assert.match(draftDialogSource, /agreement-correction-unchanged-fields/);
		assert.match(draftDialogSource, /leaseManagements\.cancelAgreementSuccessorDraft/);
		assert.match(draftDialogSource, /agreement-cancel-draft-reason/);
		assert.match(detailPageSource, /source=\{editAgreementSource\}/);
	});

	it('pages agreement history on the server and keeps artifacts distinct', () => {
		assert.match(detailPageSource, /skip: agreementSkip/);
		assert.match(detailPageSource, /take: AGREEMENT_PAGE_SIZE/);
		assert.match(detailPageSource, /<Pagination bind:skip=\{agreementSkip\}/);
		assert.doesNotMatch(detailPageSource, /take:\s*100/);
		assert.match(detailPageSource, /> Issued PDF</);
		assert.match(detailPageSource, /> Executed PDF</);
		assert.match(detailPageSource, /agreement\.hasSourceScan/);
		assert.match(
			detailPageSource,
			/href=\{activeExperience === 'Leasing' \? `\/leasing\/rentals\/\$\{summary\.unitId\}` : `\/units\/\$\{summary\.unitId\}`\}/
		);
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
		assert.match(
			detailPageSource,
			/<PossessionActions \{summary\} canManage=\{canManageHousehold\} onchanged=\{refreshLease\} \/>/,
		);
	});

	it('formats lease-detail planned date-only fields without local timezone drift', () => {
		assert.match(detailPageSource, /import \{ formatDateOnly \} from '\$lib\/utils\/date';/);
		assert.match(
			detailPageSource,
			/Possession planned \$\{formatDateOnly\(summary\.plannedPossessionAtUtc\)\}/
		);
		assert.match(
			detailPageSource,
			/Move-out planned \{formatDateOnly\(summary\.plannedMoveOutAtUtc\)\}/
		);
		assert.doesNotMatch(
			detailPageSource,
			/new Date\(summary\.plannedPossessionAtUtc\)\.toLocaleDateString/
		);
		assert.doesNotMatch(
			detailPageSource,
			/new Date\(summary\.plannedMoveOutAtUtc\)\.toLocaleDateString/
		);
	});

	it('records the approved ending disposition without mutating an agreement', () => {
		assert.match(endpointSource, /recordEndingDisposition:/);
		assert.match(endpointSource, /\/ending-disposition`/);
		for (const disposition of ['Undecided', 'OfferRenewal', 'OfferMonthToMonth', 'NonRenewalMoveOut']) {
			assert.match(endingDispositionSource, new RegExp(`value: '${disposition}'`));
		}
		assert.match(endingDispositionSource, /noticeGivenAtUtc/);
		assert.match(endingDispositionSource, /plannedMoveOutAtUtc/);
		assert.match(endingDispositionSource, /decisionReason/);
		assert.match(endingDispositionSource, /does not change the signed lease/i);
		assert.match(detailPageSource, /<EndingDispositionDialog/);
	});
});
