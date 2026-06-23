import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { ScanFieldDto } from '$lib/api/scan';
import { STEP_FIELD_TO_EXTRACTION, toLeasePrefill } from './lease-prefill.ts';

function field(name: string, value: string): ScanFieldDto {
	return { name, value, confidence: 1 };
}

test('toLeasePrefill carries extracted tenant contact fields into guided setup', () => {
	const { values } = toLeasePrefill([
		field('tenant_name', 'Maya Ortiz'),
		field('tenant_email', 'maya.ortiz.pass39@example.local'),
		field('tenant_phone', '614-555-0139'),
		field('tenant_emergency_contact', 'Luis Ortiz, 614-555-0140'),
	]);

	assert.equal(values.tenantName, 'Maya Ortiz');
	assert.equal(values.tenantEmail, 'maya.ortiz.pass39@example.local');
	assert.equal(values.tenantPhone, '614-555-0139');
	assert.equal(values.tenantEmergencyContact, 'Luis Ortiz, 614-555-0140');
	assert.equal(STEP_FIELD_TO_EXTRACTION.email, 'tenant_email');
	assert.equal(STEP_FIELD_TO_EXTRACTION.phone, 'tenant_phone');
	assert.equal(STEP_FIELD_TO_EXTRACTION.emergencyContact, 'tenant_emergency_contact');
});
