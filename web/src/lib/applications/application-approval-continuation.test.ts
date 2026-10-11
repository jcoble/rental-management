import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import {
	prepareMoveInHrefForApprovedTenant,
	readPrepareMoveInPrefill
} from '../leases/prepare-move-in-prefill.ts';

// The approved-application continuation lives in the extracted ApplicationDetail record
// component. The clean lease-foundation cutover sends it to Prepare move-in; it must never
// reopen the removed generic lease-create modal.
const applicationDetailSource = readFileSync(
	new URL('../components/records/ApplicationDetail.svelte', import.meta.url),
	'utf8'
);
const leasesPageSource = readFileSync(
	new URL('../../routes/(protected)/leases/+page.svelte', import.meta.url),
	'utf8'
);
const leasingApplicationDetailRouteSource = readFileSync(
	new URL('../../routes/(protected)/leasing/applications/[id]/+page.svelte', import.meta.url),
	'utf8'
);
const leasingListPageSource = readFileSync(
	new URL('../components/leasing/LeasingListPage.svelte', import.meta.url),
	'utf8'
);

describe('approved application lease continuation', () => {
	it('builds and reads a safe Prepare move-in prefill URL', () => {
		assert.equal(
			prepareMoveInHrefForApprovedTenant(42, 17, 9),
			'/applications?prepareMoveIn=1&tenantId=42&applicationId=17&unitId=9'
		);
		assert.equal(
			prepareMoveInHrefForApprovedTenant(42, 17, 9, '/leasing/applications/17'),
			'/leasing/applications/17?prepareMoveIn=1&tenantId=42&applicationId=17&unitId=9'
		);
		assert.deepEqual(
			readPrepareMoveInPrefill(
				new URLSearchParams('prepareMoveIn=1&tenantId=42&applicationId=17&unitId=9')
			),
			{
				tenantId: '42',
				applicationId: '17',
				unitId: '9'
			}
		);
		assert.equal(readPrepareMoveInPrefill(new URLSearchParams('tenantId=42')), null);
		assert.deepEqual(
			readPrepareMoveInPrefill(new URLSearchParams('prepareMoveIn=1&tenantId=abc')),
			{
				tenantId: '',
				applicationId: '',
				unitId: ''
			}
		);
	});

	it('offers a Prepare move-in continuation from the approved application banner', () => {
		assert.match(
			applicationDetailSource,
			/prepareMoveInHrefForApprovedTenant\(tenantLinkId, id, application\?\.unitId \?\? '', prepareMoveInBasePath\)/
		);
		assert.match(applicationDetailSource, /prepareMoveInBasePath = '\/applications'/);
		assert.match(applicationDetailSource, /data-testid="application-prepare-move-in"/);
		assert.match(applicationDetailSource, /> Prepare move-in <ArrowRight/);
	});

	it('keeps leasing application navigation inside the leasing experience', () => {
		assert.match(leasingListPageSource, /return `\/leasing\/applications\/\$\{item\.recordId\}`/);
		assert.match(leasingApplicationDetailRouteSource, /<ApplicationDetail/);
		assert.match(leasingApplicationDetailRouteSource, /leasingWorkspace\.application\(id\)/);
		assert.match(leasingApplicationDetailRouteSource, /prepareMoveInBasePath/);
		assert.match(leasingApplicationDetailRouteSource, /showScreening=\{false\}/);
		assert.match(leasingApplicationDetailRouteSource, /applicationQueryScope="leasing"/);
		assert.match(leasingApplicationDetailRouteSource, /readPrepareMoveInPrefill\(page\.url\.searchParams\)/);
		assert.match(leasingApplicationDetailRouteSource, /<PrepareMoveInDialog/);
		assert.match(leasingApplicationDetailRouteSource, /goto\(`\/leasing\/move-ins\/\$\{result\.leaseManagementId\}`\)/);
		assert.doesNotMatch(leasingApplicationDetailRouteSource, /applications\.get/);
		assert.doesNotMatch(leasingApplicationDetailRouteSource, /applications\.screening/);
		assert.doesNotMatch(leasingApplicationDetailRouteSource, /leaseManagements\.prepareMoveIn/);
		assert.doesNotMatch(leasingApplicationDetailRouteSource, /applications\?prepareMoveIn/);
	});

	it('does not restore the removed generic lease-create modal', () => {
		assert.match(leasesPageSource, /data-testid="leases-create-lease"/);
		assert.match(leasesPageSource, /<PrepareMoveInDialog[\s\S]*mode="manual"/);
		assert.doesNotMatch(leasesPageSource, /readPrepareMoveInPrefill/);
		assert.doesNotMatch(leasesPageSource, /openCreate/);
		assert.doesNotMatch(leasesPageSource, /leases\.create/);
	});
});
