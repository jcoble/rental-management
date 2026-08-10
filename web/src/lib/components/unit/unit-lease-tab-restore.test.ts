import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const leaseTabSource = readFileSync(new URL('./tabs/LeaseTab.svelte', import.meta.url), 'utf8');
const unitRouteSource = readFileSync(new URL('../../../routes/(protected)/units/[id]/+page.svelte', import.meta.url), 'utf8');
const detailSource = readFileSync(new URL('../leases/LeaseManagementDetail.svelte', import.meta.url), 'utf8');
const draftSource = readFileSync(new URL('../leases/AgreementDraftDialog.svelte', import.meta.url), 'utf8');
const signatureSource = readFileSync(new URL('../leases/AgreementSignatureProgress.svelte', import.meta.url), 'utf8');

describe('unit Tenant & lease canonical action surface', () => {
	it('deep-links the selected LeaseManagement record into the canonical detail component', () => {
		assert.match(
			leaseTabSource,
			/href=\{`\/units\/\$\{dashboard\.unit\.id\}\?tab=tenant-lease&view=agreements&leaseManagement=\$\{relationship\.leaseManagementId\}`\}/
		);
		assert.match(unitRouteSource, /const selectedLeaseManagementId = \$derived\.by\(\(\) =>/);
		assert.match(unitRouteSource, /page\.url\.searchParams\.get\('leaseManagement'\)/);
		assert.match(unitRouteSource, /<LeaseManagementDetail leaseManagementId=\{selectedLeaseManagementId\} \/>/);
		assert.match(unitRouteSource, /params\.delete\('leaseManagement'\)/);
	});

	it('keeps every requested lease action in the existing canonical detail/dialog flows', () => {
		assert.match(detailSource, /<LeaseArtifactActions/);
		assert.match(detailSource, /leaseManagements\.downloadArtifact\(/);
		assert.match(detailSource, /URL\.createObjectURL\(blob\)/);
		assert.match(detailSource, /window\.open\('', '_blank'/);
		assert.match(detailSource, /<AddendumCreateDialog/);
		assert.match(detailSource, /<AgreementSuccessorDialog/);
		assert.match(detailSource, /<AgreementDraftDialog/);
		assert.match(detailSource, /<AgreementSignatureProgress/);
		assert.match(draftSource, /prepareAgreementIssuance/);
		assert.match(signatureSource, /resendAgreementInvitation/);
		for (const label of [
			'Fix a typo',
			'Rewrite the whole lease',
			'Renew it',
			'Switch to month-to-month',
			'Add a page'
		]) {
			assert.match(detailSource, new RegExp(`>\\s*${label}<`));
		}
		assert.match(detailSource, /No signed lease document yet/);
	});
});
