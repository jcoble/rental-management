import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const endpointSource = readFileSync(
	new URL('../api/endpoints/lease-managements.ts', import.meta.url),
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

describe('lease renewal effective addendum decisions', () => {
	it('loads the server-authoritative effective series with a typed endpoint', () => {
		assert.match(endpointSource, /interface LeaseAgreementEffectiveAddendumSeries/);
		assert.match(endpointSource, /requiredDecisionCount: number/);
		assert.match(endpointSource, /financialEffects: LeaseAgreementRenewalFinancialEffectSummary\[\]/);
		assert.match(endpointSource, /getEffectiveAddendumSeries:/);
		assert.match(endpointSource, /\/effective-addendum-series`/);
		assert.match(successorDialogSource, /leaseManagements\.getEffectiveAddendumSeries/);
		assert.match(successorDialogSource, /enabled: isRenewal/);
	});

	it('requires and explains one explicit decision for every required returned series', () => {
		assert.match(successorDialogSource, /for \(const series of effectiveSeries\.series\)/);
		assert.match(successorDialogSource, /if \(!series\.decisionRequired\) continue/);
		assert.match(successorDialogSource, /Choose what happens to \$\{series\.title\}/);
		assert.match(successorDialogSource, /option value="End"/);
		assert.match(successorDialogSource, /option value="IncorporateIntoBase"/);
		assert.match(successorDialogSource, /option value="ReissueAsAddendum"/);
		assert.match(successorDialogSource, /Stops this addendum when the new agreement begins/);
		assert.match(successorDialogSource, /folded into the new base agreement/);
		assert.match(successorDialogSource, /Creates a new editable addendum draft/);
	});

	it('submits exact series identifiers only for renewals and does not use relationship-wide counts', () => {
		assert.match(successorDialogSource, /if \(!isRenewal\) return \[\]/);
		assert.match(successorDialogSource, /sourceAddendumSeriesPublicId: series\.seriesPublicId/);
		assert.doesNotMatch(successorDialogSource, /addendumDecisionBlocked/);
		assert.doesNotMatch(successorDialogSource, /addendumCount/);
		assert.doesNotMatch(detailPageSource, /addendumCount=\{detail\.addendumCount\}/);
	});
});
