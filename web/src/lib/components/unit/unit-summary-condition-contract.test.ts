import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';

const overview = readFileSync(new URL('./tabs/OverviewTab.svelte', import.meta.url), 'utf8');
const service = readFileSync(
	new URL('../../../../../RentalCommand.Api/Services/Domain/UnitDashboardService.cs', import.meta.url),
	'utf8',
);

describe('Unit Summary condition contract', () => {
	test('keeps five condition families independent', () => {
		for (const condition of [
			'occupancyPossession',
			'marketingAvailability',
			'tenantAccountCondition',
			'legalNoticeCondition',
			'maintenanceTurnover',
		]) {
			assert.match(overview, new RegExp(`dashboard\\.${condition}`));
		}
	});

	test('does not infer no tenancy from null Agreement', () => {
		assert.match(service, /OccupancyPossession = new UnitOccupancyPossessionCondition/);
		assert.match(service, /LegalNoticeCondition = new UnitLegalNoticeCondition/);
		assert.doesNotMatch(service, /AgreementId is null[\\s\\S]{0,80}(?:IsOccupied|LeaseManagementId|TenantAccountId) =/);
	});
});
