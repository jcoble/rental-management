import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const endpointSource = readFileSync(
	new URL('../api/endpoints/lease-addendums.ts', import.meta.url),
	'utf8'
);
const leaseManagementEndpointSource = readFileSync(
	new URL('../api/endpoints/lease-managements.ts', import.meta.url),
	'utf8'
);
const detailPageSource = readFileSync(
	new URL('../../routes/(protected)/leases/[id]/+page.svelte', import.meta.url),
	'utf8'
);
const draftDialogSource = readFileSync(
	new URL('../components/leases/AddendumDraftDialog.svelte', import.meta.url),
	'utf8'
);
const createDialogSource = readFileSync(
	new URL('../components/leases/AddendumCreateDialog.svelte', import.meta.url),
	'utf8'
);
const correctionDialogSource = readFileSync(
	new URL('../components/leases/AddendumCorrectionDialog.svelte', import.meta.url),
	'utf8'
);

describe('canonical lease addendum lifecycle hub', () => {
	it('uses the server-paged addendum history contract without client list shaping', () => {
		assert.match(endpointSource, /listPage:/);
		assert.match(endpointSource, /\/addenda\/page\$\{historyQuery\(params\)\}/);
		assert.match(detailPageSource, /leaseAddendums\.listPage/);
		assert.match(detailPageSource, /skip: addendumSkip/);
		assert.match(detailPageSource, /take: ADDENDUM_PAGE_SIZE/);
		assert.match(detailPageSource, /sort: '-effectiveFromOn'/);
		assert.match(detailPageSource, /addendum-history-pagination/);
		assert.doesNotMatch(detailPageSource, /addendaQuery\.data[^\n]*\.sort\(/);
		assert.doesNotMatch(detailPageSource, /addendaQuery\.data[^\n]*\.filter\(/);
	});

	it('reloads and edits the exact canonical draft revision', () => {
		assert.match(endpointSource, /getDraft:/);
		assert.match(endpointSource, /editDraft:/);
		assert.match(endpointSource, /\/addenda\/\$\{leaseAddendumId\}\/draft/);
		assert.match(draftDialogSource, /leaseAddendums\.getDraft/);
		assert.match(draftDialogSource, /draftRevision: draft\.draftRevision/);
		assert.match(draftDialogSource, /termsSchemaVersion: draft\.termsSchemaVersion/);
		assert.match(draftDialogSource, /termsPayload: draft\.termsPayload/);
		assert.match(draftDialogSource, /leaseAddendums\.editDraft/);
		assert.match(draftDialogSource, /isRequired: true/);
		assert.match(draftDialogSource, /draft\.signers\.some\(\(signer\) => !signer\.isRequired\)/);
		assert.doesNotMatch(draftDialogSource, /bind:checked=\{signer\.isRequired\}/);
	});

	it('prepares and issues one saved revision and keeps issued and executed artifacts distinct', () => {
		assert.match(endpointSource, /prepareIssuance:/);
		assert.match(endpointSource, /\/issuance-preparations`/);
		assert.match(endpointSource, /issue:/);
		assert.match(draftDialogSource, /leaseAddendums\.prepareIssuance/);
		assert.match(draftDialogSource, /\{ \.\.\.prepared, subject: subject\.trim\(\) \}/);
		assert.match(draftDialogSource, /Save the current draft changes before issuing/);
		assert.match(detailPageSource, /> Issued PDF</);
		assert.match(detailPageSource, /> Executed PDF</);
		assert.match(detailPageSource, /leaseAddendums\.downloadArtifact/);
	});

	it('uses canonical create and correction commands and opens their returned exact draft', () => {
		assert.match(endpointSource, /createDraft:/);
		assert.match(endpointSource, /correctDraft:/);
		assert.match(endpointSource, /\/addenda\/\$\{sourceAddendumId\}\/correct/);
		assert.match(createDialogSource, /leaseAddendums\.createDraft/);
		assert.match(createDialogSource, /documentTemplates\.listPage/);
		assert.match(createDialogSource, /leaseManagements\.getCurrentPartiesContext/);
		assert.match(createDialogSource, /party\.role === 'PrimaryTenant' \|\| party\.role === 'CoTenant'/);
		assert.match(createDialogSource, /isRequired: true/);
		assert.doesNotMatch(createDialogSource, /bind:checked=\{signer\.isRequired\}/);
		assert.match(leaseManagementEndpointSource, /\/return-possession-context`/);
		assert.match(correctionDialogSource, /leaseAddendums\.correctDraft/);
		assert.match(detailPageSource, /editAddendumId = result\.leaseAddendumId/);
	});
});
