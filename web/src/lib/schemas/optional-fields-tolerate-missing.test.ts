/**
 * Regression test for the "Save silently does nothing" class of bug.
 *
 * The optional zod helpers (optionalText / idString / optionalNonNegative / …)
 * used `.nullable()` but not `.optional()`, so they rejected a MISSING key
 * (`undefined`). Any form that validates a *subset* of its schema — because the
 * schema grew (expenses) or the page drops a field before validating (leases) —
 * errored on the absent keys, parseForm returned errors on fields that aren't
 * rendered, and Save bailed with no request. Hardening the optional helpers to
 * accept `undefined` fixes the whole class.
 *
 *   node --test --experimental-strip-types src/lib/schemas/optional-fields-tolerate-missing.test.ts
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';

import { expenseSchema, leaseSchema, parseForm } from './index.ts';

test('a schema tolerates a form missing its optional/extra keys (expenses repro)', () => {
	// The expense DETAIL form: omits workOrderId + the scan/receipt fields.
	const partial = {
		description: 'Drain repair',
		amount: '42.50',
		incurredAt: '2026-01-01',
		category: 'Repairs',
		status: 'Pending',
		propertyId: '',
		vendorId: '',
		dueDate: '',
		paidAt: '',
		billableToOwner: false,
		notes: '',
		subtotal: '',
		taxAmount: ''
	};
	const r = parseForm(expenseSchema, partial);
	assert.equal(r.errors, null, `unexpected errors: ${JSON.stringify(r.errors)}`);
});

test('leases submit validates rest against leaseSchema.omit({propertyId}) — no propertyId error', () => {
	// Mirrors the fixed leases/[id] submit(): propertyId is immutable post-creation, dropped from
	// the payload AND omitted from validation. Validating against the full leaseSchema (which
	// requires propertyId) was the bug — it always errored on the dropped key.
	const rest = {
		leaseNumber: 'L-1',
		unitId: '1',
		tenantId: '1',
		startDate: '2026-01-01',
		endDate: '2026-12-31',
		monthlyRent: '1000',
		securityDeposit: '1000',
		lateFeeAmount: '50',
		rentDueDay: '1',
		status: 'Active',
		notes: ''
	};
	const r = parseForm(leaseSchema.omit({ propertyId: true }), rest);
	assert.equal(r.errors, null, `unexpected errors: ${JSON.stringify(r.errors)}`);
});

test('lease terms validate without legacy rent-generation controls', () => {
	const r = parseForm(leaseSchema, {
		leaseNumber: 'L-1',
		propertyId: '1',
		unitId: '1',
		tenantId: '1',
		startDate: '2026-01-01',
		endDate: '2026-12-31',
		monthlyRent: '1000',
		securityDeposit: '1000',
		lateFeeAmount: '50',
		rentDueDay: '1',
		status: 'Active',
		notes: ''
	});
	assert.equal(r.errors, null, `unexpected errors: ${JSON.stringify(r.errors)}`);
});

test('required fields still error when absent (hardening is optional-only)', () => {
	// description + incurredAt are required; this must still fail.
	const r = parseForm(expenseSchema, { amount: '10', category: 'Repairs', status: 'Pending', billableToOwner: false });
	assert.ok(r.errors, 'expected validation errors for missing required fields');
	assert.ok(r.errors?.description, 'description must still be required');
	assert.ok(r.errors?.incurredAt, 'incurredAt must still be required');
});
