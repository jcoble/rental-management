import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync(
	new URL('../../routes/(protected)/tenants/[id]/+page.svelte', import.meta.url),
	'utf8'
);

describe('tenant create/send notice editing', () => {
	it('lets generated notice drafts be edited before sending', () => {
		assert.match(source, /let noticeEdits = \$state/);
		assert.match(source, /data-testid="tenant-notice-edit-subject-\{draft\.id\}"/);
		assert.match(source, /data-testid="tenant-notice-edit-body-\{draft\.id\}"/);
		assert.match(source, /notices\.update\(draft\.id, editedNoticePayload\(draft\)\)/);
		assert.match(source, /notices\.approve\(draft\.id/);
		assert.match(source, /onclick=\{\(\) => sendNoticeMutation\.mutate\(draft\)\}/);
	});
});
