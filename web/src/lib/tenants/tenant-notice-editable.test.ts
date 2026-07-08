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

describe('tenant create/send notice editing', () => {
	it('lets generated notice drafts be edited before sending', () => {
		assert.match(routeSource, /import TenantNoticeDialog from '\$lib\/components\/notices\/TenantNoticeDialog\.svelte'/);
		assert.match(routeSource, /<TenantNoticeDialog/);
		assert.match(dialogSource, /let noticeEdits = \$state/);
		assert.match(dialogSource, /data-testid="tenant-notice-edit-subject-\{draft\.id\}"/);
		assert.match(dialogSource, /data-testid="tenant-notice-edit-body-\{draft\.id\}"/);
		// PATCH is skipped when the user did not edit the generated copy;
		// the conditional guards the call.
		assert.match(dialogSource, /if \(changed\).*notices\.update\(draft\.id/s);
		assert.match(dialogSource, /notices\.approve\(draft\.id/);
		assert.match(dialogSource, /onclick=\{\(\) => sendNoticeMutation\.mutate\(draft\)\}/);
	});
});
