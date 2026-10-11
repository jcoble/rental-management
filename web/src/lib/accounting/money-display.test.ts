import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import {
	formatMoneyCategoryLabel,
	formatMoneyEntryLabel,
	formatRentalLocation,
	formatResidentName,
} from './money-display.ts';

const source = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8');

describe('money display labels', () => {
	it('replaces internal money-entry names with ordinary language', () => {
		assert.equal(formatMoneyEntryLabel('PaymentReceipt'), 'Payment received');
		assert.equal(formatMoneyEntryLabel('TenantLedger'), 'Rent or resident charge');
		assert.equal(formatMoneyEntryLabel('LateFeeCharge'), 'Late fee');
		assert.equal(formatMoneyEntryLabel('ApplicationFee'), 'Application fee');
	});

	it('turns unknown identifiers into readable fallback labels', () => {
		assert.equal(formatMoneyEntryLabel('ManualAdjustment'), 'Manual adjustment');
		assert.equal(formatMoneyCategoryLabel('CleaningMaintenance'), 'Cleaning & maintenance');
		assert.equal(formatMoneyCategoryLabel('DepositCharge'), 'Security deposit');
		assert.equal(formatMoneyCategoryLabel('AddendumCharge'), 'Lease/addendum charge');
		assert.equal(formatMoneyCategoryLabel('ManualCharge'), 'Manual/other charge');
	});

	it('builds rental context from names instead of internal ids', () => {
		assert.equal(
			formatRentalLocation({ propertyName: 'Clintonville Townhome', unitNumber: 'Main' }),
			'Clintonville Townhome · Unit Main'
		);
		assert.equal(formatRentalLocation({}), 'Property');
		assert.equal(formatResidentName('Jesse Coble'), 'Jesse Coble');
		assert.equal(formatResidentName(null), 'Tenant not listed');
	});
});

describe('Lane 3A presentation contracts', () => {
	it('uses the app select control for Year End and AI connection settings', () => {
		const yearEnd = source('../../routes/(protected)/accounting/year-end/+page.svelte');
		const aiConnection = source('../../routes/(protected)/settings/integrations/ai/+page.svelte');

		assert.doesNotMatch(yearEnd, /<select\b/);
		assert.doesNotMatch(aiConnection, /<select\b/);
		assert.match(yearEnd, /<Select\.Root/);
		assert.match(aiConnection, /<Select\.Root/);
	});

	it('keeps deployment secrets and environment names out of Banking copy', () => {
		const banking = source('../../routes/(protected)/banking/+page.svelte');

		assert.doesNotMatch(banking, /Plaid:ClientId|Plaid:Secret|Environment:/);
		assert.match(banking, /Bank connections are not available yet/);
	});

	it('keeps deposit instructions aligned with the current live actions', () => {
		const article = source('../../../../RentalCommand.Api/KnowledgeBase/security-deposits.md');

		assert.match(article, /\*\*Record funds\*\*/);
		assert.match(article, /\*\*Add deduction\*\*/);
		assert.match(article, /\*\*Record refund\*\*/);
		assert.match(article, /\*\*Move-out statement\*\*/);
		assert.doesNotMatch(article, /New Holding|Create Holding/);
	});
});
