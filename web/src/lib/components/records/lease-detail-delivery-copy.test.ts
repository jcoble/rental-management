import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const source = readFileSync(new URL('./LeaseDetail.svelte', import.meta.url), 'utf8');

test('delivery-disabled lease attempts read as per-attempt failures, not a global email shutoff', () => {
	assert.doesNotMatch(source, /delivery\s+is\s+disabled\s+right\s+now/i);
	assert.doesNotMatch(source, new RegExp(['configure', 'email', 'delivery'].join('\\s+'), 'i'));
	assert.match(source, /that email attempt was not delivered/);
	assert.match(source, /Resend for signature to try the configured email provider again/);
	assert.match(source, /if \(status === 'DeliveryDisabled'\) return 'Not delivered'/);
});
