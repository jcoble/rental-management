import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, it } from 'node:test';

const datePickerSource = readFileSync(new URL('./DatePicker.svelte', import.meta.url), 'utf8');
const rangeDatePickerSource = readFileSync(new URL('./RangeDatePicker.svelte', import.meta.url), 'utf8');
const inlineFieldSource = readFileSync(new URL('./InlineField.svelte', import.meta.url), 'utf8');
const maintenanceSource = readFileSync(
	new URL('../../../routes/(protected)/maintenance/+page.svelte', import.meta.url),
	'utf8'
);
const leaseTermFieldsSource = readFileSync(
	new URL('../forms/LeaseTermFields.svelte', import.meta.url),
	'utf8'
);
const oneTimeChargeSource = readFileSync(
	new URL('../accounting/OneTimeChargeSheet.svelte', import.meta.url),
	'utf8'
);
const scanReviewSource = readFileSync(
	new URL('../../../routes/(protected)/scan/[draftId]/+page.svelte', import.meta.url),
	'utf8'
);
const calendarSelectSources = [
	'../ui/calendar/calendar-month-select.svelte',
	'../ui/calendar/calendar-year-select.svelte',
	'../ui/range-calendar/range-calendar-month-select.svelte',
	'../ui/range-calendar/range-calendar-year-select.svelte'
].map((path) => readFileSync(new URL(path, import.meta.url), 'utf8'));

function svelteFilesUnder(directory: string): string[] {
	const files: string[] = [];
	for (const entry of readdirSync(directory, { withFileTypes: true })) {
		const path = join(directory, entry.name);
		if (entry.isDirectory()) files.push(...svelteFilesUnder(path));
		else if (entry.isFile() && path.endsWith('.svelte')) files.push(path);
	}
	return files;
}

describe('date picker wiring', () => {
	it('uses the shared mask helper and exposes a compact Today shortcut', () => {
		assert.match(datePickerSource, /maskDateInput/);
		assert.match(datePickerSource, /todayValue\?: string/);
		assert.match(datePickerSource, /todayValue\?\.trim\(\) \|\| today\(getLocalTimeZone\(\)\)\.toString\(\)/);
		assert.match(datePickerSource, /data-testid=\{testid \? `\$\{testid\}-today` : undefined\}/);
	});

	it('exposes the DatePicker invalid state for submit guards', () => {
		assert.match(datePickerSource, /invalid = \$bindable\(false\)/);
		assert.match(datePickerSource, /invalid\?: boolean/);
		assert.match(datePickerSource, /aria-invalid=\{invalid\}/);
	});

	it('exposes caller-controlled invalid state on range date fields', () => {
		assert.match(rangeDatePickerSource, /invalid = \$bindable\(false\)/);
		assert.match(rangeDatePickerSource, /invalid\?: boolean/);
		assert.match(rangeDatePickerSource, /aria-invalid=\{invalid\}/);
	});

	it('binds every one-time charge date to invalid state and blocks an invalid submit', () => {
		for (const invalidState of [
			'effectiveDateInvalid',
			'dueDateInvalid',
			'servicePeriodStartInvalid',
			'servicePeriodEndInvalid'
		]) {
			assert.match(oneTimeChargeSource, new RegExp(`bind:invalid=\\{${invalidState}\\}`));
		}
		assert.match(oneTimeChargeSource, /const datePickerInvalid = \$derived\(/);
		assert.match(oneTimeChargeSource, /disabled=\{mutation\.isPending \|\| datePickerInvalid\}/);
		assert.match(oneTimeChargeSource, /if \(servicePeriodStartInvalid \|\| servicePeriodEndInvalid\)/);
	});

	it('routes dynamic scan-review date fields through DatePicker and its submit guard', () => {
		assert.match(scanReviewSource, /const SCAN_DATE_FIELDS = new Set\(\['due_date', 'transaction_date'\]\)/);
		assert.match(scanReviewSource, /term\.type === 'date'[\s\S]*<DatePicker/);
		assert.match(scanReviewSource, /appField\.type === 'date'[\s\S]*<DatePicker/);
		assert.match(scanReviewSource, /statementField\.type === 'date'[\s\S]*<DatePicker/);
		assert.match(scanReviewSource, /loanField\.type === 'date'[\s\S]*<DatePicker/);
		assert.match(scanReviewSource, /isScanDateField\(fieldName\)[\s\S]*<DatePicker/);
		assert.match(scanReviewSource, /isScanDateField\(field\.name\)[\s\S]*<DatePicker/);
		assert.match(scanReviewSource, /bind:invalid=\{scanDateInvalid\[/);
		assert.match(scanReviewSource, /disabled=\{confirmMutation\.isPending[\s\S]*scanDatePickerInvalid/);
	});

	it('keeps the formatted date ahead of compact DatePicker actions', () => {
		assert.match(datePickerSource, /date-picker-shell relative w-full min-w-40/);
		assert.match(datePickerSource, /container: date-picker \/ inline-size/);
		assert.match(datePickerSource, /\.date-picker-input[\s\S]*padding-right: 4\.25rem/);
		assert.match(datePickerSource, /\.date-picker-today[\s\S]*font-size: 0/);
		assert.match(datePickerSource, /date-picker-calendar-trigger/);
	});

	it('has no native date, datetime-local, or month input in a Svelte source file', () => {
		const webSrcRoot = fileURLToPath(new URL('../../../', import.meta.url));
		const nativeDateInput = /<(?:input|Input)\b[^>]*\btype\s*=\s*["'](?:date|datetime-local|month)["']/i;
		for (const file of svelteFilesUnder(webSrcRoot)) {
			const source = readFileSync(file, 'utf8');
			assert.doesNotMatch(source, nativeDateInput, file);
		}
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

	it('uses the app select menu for calendar month and year choices', () => {
		for (const source of calendarSelectSources) {
			assert.match(source, /SimpleSelect/);
			assert.doesNotMatch(source, /<select\b/);
		}
	});

	it('defaults an empty lease end date to one calendar year after the start date', () => {
		assert.match(leaseTermFieldsSource, /addCalendarYear/);
		assert.match(leaseTermFieldsSource, /function handleStartDateChange/);
		assert.match(leaseTermFieldsSource, /onchange=\{handleStartDateChange\}/);
	});
});
