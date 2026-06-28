import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import {
	leaseCreateHrefForApprovedTenant,
	readLeaseCreatePrefill,
} from '../leases/lease-create-prefill.ts';

// TSK-457: the approved-application create-lease banner moved out of the thin [id] route
// wrapper into the extracted ApplicationDetail record component.
const applicationDetailSource = readFileSync(
	new URL('../components/records/ApplicationDetail.svelte', import.meta.url),
	'utf8'
);
const leasesPageSource = readFileSync(
	new URL('../../routes/(protected)/leases/+page.svelte', import.meta.url),
	'utf8'
);

describe('approved application lease continuation', () => {
	it('builds and reads a safe create-lease prefill URL', () => {
		assert.equal(leaseCreateHrefForApprovedTenant(42), '/leases?create=1&tenantId=42');
		assert.deepEqual(readLeaseCreatePrefill(new URLSearchParams('create=1&tenantId=42')), {
			tenantId: '42',
		});
		assert.equal(readLeaseCreatePrefill(new URLSearchParams('tenantId=42')), null);
		assert.deepEqual(readLeaseCreatePrefill(new URLSearchParams('create=1&tenantId=abc')), {
			tenantId: '',
		});
	});

	it('offers a create-lease continuation from the approved application banner', () => {
		assert.match(applicationDetailSource, /leaseCreateHrefForApprovedTenant\(tenantLinkId\)/);
		assert.match(applicationDetailSource, /data-testid="application-create-lease"/);
	});

	it('opens the lease create modal with the approved tenant preselected', () => {
		assert.match(leasesPageSource, /readLeaseCreatePrefill\(page\.url\.searchParams\)/);
		assert.match(leasesPageSource, /openCreate\(\{ tenantId: prefill\.tenantId \}\)/);
	});
});
