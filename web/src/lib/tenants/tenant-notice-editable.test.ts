import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const routeSource = readFileSync(
	new URL('../../routes/(protected)/tenants/[id]/+page.svelte', import.meta.url),
	'utf8'
);
const dialogSource = readFileSync(
	new URL('../components/notices/TenantNoticeDialog.svelte', import.meta.url),
	'utf8'
);
const leaseManagementDetailSource = readFileSync(
	new URL('../components/leases/LeaseManagementDetail.svelte', import.meta.url),
	'utf8'
);

describe('tenant create/send notice editing', () => {
	it('lets generated notice drafts be edited before sending', () => {
		assert.match(routeSource, /import TenantNoticeDialog from '\$lib\/components\/notices\/TenantNoticeDialog\.svelte'/);
		assert.match(routeSource, /<TenantNoticeDialog/);
		assert.match(dialogSource, /let noticeEdits = \$state/);
		assert.match(dialogSource, /data-testid="tenant-notice-edit-subject-\{draft\.id\}"/);
		assert.match(dialogSource, /data-testid="tenant-notice-edit-body-\{draft\.id\}"/);
		assert.match(dialogSource, /notifications\.tenantNotices\.previewRecipients/);
		assert.match(dialogSource, /data-testid="tenant-notice-channel-portal-\{draft\.id\}"/);
		assert.match(dialogSource, /data-testid="tenant-notice-channel-email-\{draft\.id\}"/);
		assert.match(dialogSource, /data-testid="tenant-notice-channel-sms-\{draft\.id\}"/);
		// PATCH is skipped when the user did not edit the generated copy;
		// the conditional guards the call.
		assert.match(dialogSource, /if \(changed\).*notices\.update\(draft\.id/s);
		assert.match(dialogSource, /notices\.approve\(draft\.id/);
		assert.match(dialogSource, /onclick=\{\(\) => sendNoticeMutation\.mutate\(draft\)\}/);
	});

	it('supports canonical recipient-scoped generation without resending existing notices', () => {
		assert.match(dialogSource, /recipientTenantId\?: number/);
		assert.match(dialogSource, /function generateRequest\(noticeType\?: string\)/);
		assert.match(dialogSource, /notices\.generate\(generateRequest\(noticeType\)\)/);
		assert.match(dialogSource, /\.\.\.\(recipientTenantId > 0 \? \{ recipientTenantId \} : \{\}\)/);
		assert.match(dialogSource, /draft\.status === 'Draft'/);
		assert.match(dialogSource, /disabled=\{!editable\}/);
		assert.match(dialogSource, /This notice already exists and will not be sent again\./);
	});

	it('adds optional relationship scoping for lease detail without changing the Tenant-page entry', () => {
		assert.match(routeSource, /recipientTenantId=\{id\}/);
		assert.doesNotMatch(routeSource, /leaseManagementId=/);
		assert.match(dialogSource, /leaseManagementId\?: number/);
		assert.match(dialogSource, /\.\.\.\(leaseManagementId > 0 \? \{ leaseManagementId \} : \{\}\)/);
		assert.match(
			leaseManagementDetailSource,
			/import TenantNoticeDialog from '\$lib\/components\/notices\/TenantNoticeDialog\.svelte'/,
		);
		assert.match(leaseManagementDetailSource, /<TenantNoticeDialog/);
	});
});
