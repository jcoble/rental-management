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

describe('approved application lease continuation', () => {
	it('builds and reads a safe Prepare move-in prefill URL', () => {
		assert.equal(
			prepareMoveInHrefForApprovedTenant(42, 17, 9),
			'/applications?prepareMoveIn=1&tenantId=42&applicationId=17&unitId=9'
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
			/prepareMoveInHrefForApprovedTenant\(tenantLinkId, id, application\?\.unitId \?\? ''\)/
		);
		assert.match(applicationDetailSource, /data-testid="application-prepare-move-in"/);
		assert.match(applicationDetailSource, /> Prepare move-in <ArrowRight/);
	});

	it('does not restore the removed generic lease-create modal', () => {
		assert.match(
			leasesPageSource,
			/href="\/applications"[^>]*>[^<]*<Users[^>]*\/> Prepare move-in/s
		);
		assert.doesNotMatch(leasesPageSource, /readPrepareMoveInPrefill/);
		assert.doesNotMatch(leasesPageSource, /openCreate/);
		assert.doesNotMatch(leasesPageSource, /leases\.create/);
	});
});
