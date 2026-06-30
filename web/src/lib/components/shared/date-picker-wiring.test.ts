import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const datePickerSource = readFileSync(new URL('./DatePicker.svelte', import.meta.url), 'utf8');
const inlineFieldSource = readFileSync(new URL('./InlineField.svelte', import.meta.url), 'utf8');
const maintenanceSource = readFileSync(
	new URL('../../../routes/(protected)/maintenance/+page.svelte', import.meta.url),
	'utf8'
);
const leaseTermFieldsSource = readFileSync(
	new URL('../forms/LeaseTermFields.svelte', import.meta.url),
	'utf8'
);

describe('date picker wiring', () => {
	it('uses the shared mask helper and exposes a compact Today shortcut', () => {
		assert.match(datePickerSource, /maskDateInput/);
		assert.match(datePickerSource, /data-testid=\{testid \? `\$\{testid\}-today` : undefined\}/);
	});

	it('routes inline date fields through the shared DatePicker', () => {
		assert.match(inlineFieldSource, /import DatePicker from '\.\/DatePicker\.svelte'/);
		assert.match(inlineFieldSource, /type === 'date'/);
		assert.match(inlineFieldSource, /<DatePicker/);
	});

	it('routes inline and form date-time fields through the shared DateTimePicker', () => {
		assert.match(inlineFieldSource, /import DateTimePicker from '\.\/DateTimePicker\.svelte'/);
		assert.match(inlineFieldSource, /type === 'datetime-local'/);
		assert.match(inlineFieldSource, /<DateTimePicker/);
		assert.doesNotMatch(maintenanceSource, /type="datetime-local"/);
		assert.match(maintenanceSource, /<DateTimePicker[^>]+testid="work-order-scheduled-input"/);
		assert.match(maintenanceSource, /<DateTimePicker[^>]+testid="inspection-scheduled-input"/);
	});

	it('defaults an empty lease end date to one calendar year after the start date', () => {
		assert.match(leaseTermFieldsSource, /addCalendarYear/);
		assert.match(leaseTermFieldsSource, /function handleStartDateChange/);
		assert.match(leaseTermFieldsSource, /onchange=\{handleStartDateChange\}/);
	});
});
