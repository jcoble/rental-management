import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

describe('portal lease document download contract', () => {
	const endpointSource = readFileSync(
		new URL('../api/endpoints/portal.ts', import.meta.url),
		'utf8'
	);
	const pageSource = readFileSync(
		new URL('../../routes/(portal)/portal/lease/+page.svelte', import.meta.url),
		'utf8'
	);

	it('downloads through the tenant-scoped executed agreement endpoint', () => {
		assert.match(endpointSource, /downloadFile\(/);
		assert.match(endpointSource, /contentType: string/);
		assert.match(
			endpointSource,
			/\/portal\/leases\/\$\{leaseManagementId\}\/agreements\/\$\{leaseAgreementId\}\/executed-document/
		);
		assert.doesNotMatch(endpointSource, /executedStoredFileId/);
	});

	it('surfaces availability, missing state, and safe failure copy on the portal lease page', () => {
		assert.match(pageSource, /executedDocumentAvailable/);
		assert.match(pageSource, /agreement\.executedDocumentContentType/);
		assert.match(pageSource, /portal-lease-download-signed-pdf/);
		assert.match(pageSource, /Download signed lease/);
		assert.match(pageSource, /Signed lease is not available yet\. Contact management\./);
		assert.match(
			pageSource,
			/Could not download the signed lease\. Please try again or contact management\./
		);
		assert.doesNotMatch(pageSource, /Download signed lease PDF/);
		assert.doesNotMatch(pageSource, /Signed lease PDF is not available yet\. Contact management\./);
		assert.doesNotMatch(pageSource, /Could not download the signed lease PDF/);
		assert.doesNotMatch(pageSource, /executedStoredFileId/);
	});
});
