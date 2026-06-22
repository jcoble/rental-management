import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { login, unique } from './helpers';

function writeFixture(testOutputDir: string, filename: string, contents: string): string {
	fs.mkdirSync(testOutputDir, { recursive: true });
	const filePath = path.join(testOutputDir, filename);
	fs.writeFileSync(filePath, contents, 'utf8');
	return filePath;
}

async function uploadCsv(page: import('@playwright/test').Page, filePath: string) {
	const fileInput = page.getByTestId('import-file-input');
	await fileInput.setInputFiles(filePath);
}

async function dropTextFile(
	page: import('@playwright/test').Page,
	name: string,
	type: string,
	contents: string
) {
	await page.getByTestId('import-file-drop').evaluate(
		(dropZone, file) => {
			const dropped = new File([file.contents], file.name, { type: file.type });
			const dataTransfer = new DataTransfer();
			dataTransfer.items.add(dropped);
			dropZone.dispatchEvent(
				new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer })
			);
			dropZone.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer }));
		},
		{ name, type, contents }
	);
}

test.describe('CSV import workflow', () => {
	test('previews, rejects, commits, navigates, and clears stale import state', async ({
		page
	}, testInfo) => {
		const consoleErrors: string[] = [];
		const importRequests: string[] = [];
		page.on('console', (message) => {
			if (['error', 'warning'].includes(message.type())) {
				consoleErrors.push(`${message.type()}: ${message.text()}`);
			}
		});
		page.on('pageerror', (err) => consoleErrors.push(`pageerror: ${err.message}`));
		page.on('request', (request) => {
			const url = request.url();
			if (url.includes('/api/v1/import')) {
				importRequests.push(`${request.method()} ${url}`);
			}
		});

		await login(page);
		await page.goto('/import');

		await expect(page.getByTestId('import-page')).toBeVisible();
		await expect(page.getByTestId('import-title')).toHaveText('Import from a spreadsheet');
		await expect(page.getByTestId('import-entity-tenant')).toHaveAttribute('aria-pressed', 'true');
		await expect(page.getByTestId('import-columns')).toContainText(
			'first name, last name, email, phone'
		);

		const download = page.waitForEvent('download');
		await page.getByTestId('import-download-template').click();
		expect((await download).suggestedFilename()).toBe('tenant-import-template.csv');

		const importRequestCountBeforeReject = importRequests.length;
		await dropTextFile(page, 'not-a-spreadsheet.txt', 'text/plain', 'not,csv,enough');
		await expect(page.getByText('Please choose a CSV file')).toBeVisible();
		await expect(page.getByTestId('import-selected-file')).toHaveCount(0);
		expect(importRequests.length).toBe(importRequestCountBeforeReject);

		const suffix = unique('Import');
		const tenantCsv = writeFixture(
			testInfo.outputDir,
			'tenant-import-mixed.csv',
			[
				'firstName,lastName,email,phone',
				`${suffix} Tenant,Good,tenant.good.${Date.now()}@example.local,555-0100`,
				`,MissingFirst,missing.first.${Date.now()}@example.local,555-0101`
			].join('\n')
		);
		await uploadCsv(page, tenantCsv);
		await expect(page.getByTestId('import-selected-file')).toContainText('tenant-import-mixed.csv');
		await expect(page.getByTestId('import-preview')).toBeVisible();
		await expect(page.getByTestId('import-preview-summary')).toContainText('1 of 2 rows look good');
		await expect(page.getByTestId('import-row')).toHaveCount(2);
		await expect(page.getByTestId('import-row-errors')).toContainText('FirstName');

		await page.getByTestId('import-entity-property').click();
		await expect(page.getByTestId('import-entity-property')).toHaveAttribute('aria-pressed', 'true');
		await expect(page.getByTestId('import-columns')).toContainText('name, address, city, state, ZIP');
		await expect(page.getByTestId('import-selected-file')).toHaveCount(0);
		await expect(page.getByTestId('import-preview')).toHaveCount(0);

		const propertyName = `${suffix} Flats`;
		const propertyCsv = writeFixture(
			testInfo.outputDir,
			'property-import-mixed.csv',
			[
				'name,addressLine1,addressLine2,city,state,postalCode,type',
				`${propertyName},397 Import Ave,,Columbus,OH,43215,MultiFamily`,
				`${suffix} Bad Type,399 Import Ave,,Columbus,OH,43215,Castle`
			].join('\n')
		);
		await uploadCsv(page, propertyCsv);
		await expect(page.getByTestId('import-preview-summary')).toContainText('1 of 2 rows look good');
		await expect(page.getByTestId('import-row-errors')).toContainText('not a valid property type');
		await page.getByTestId('import-commit').click();
		await expect(page.getByTestId('import-result')).toBeVisible();
		await expect(page.getByTestId('import-result-summary')).toContainText('Created 1 property');
		await expect(page.getByTestId('import-result-summary')).toContainText('1 row skipped');

		await page.getByTestId('import-view-records').click();
		await expect(page).toHaveURL(/\/properties/);

		await page.goto('/import');
		await page.getByTestId('import-entity-unit').click();
		await expect(page.getByTestId('import-columns')).toContainText('property name, unit number');
		const unitCsv = writeFixture(
			testInfo.outputDir,
			'unit-import-valid.csv',
			[
				'propertyName,propertyId,unitNumber,bedrooms,bathrooms,marketRent',
				`${propertyName},,201,2,1.5,"$1,275"`
			].join('\n')
		);
		await uploadCsv(page, unitCsv);
		await expect(page.getByTestId('import-preview-summary')).toContainText('1 of 1 row looks good');
		await page.getByTestId('import-commit').click();
		await expect(page.getByTestId('import-result-summary')).toContainText('Created 1 unit');

		await page.getByTestId('import-entity-tenant').click();
		await expect(page.getByTestId('import-result')).toHaveCount(0);
		await expect(page.getByTestId('import-selected-file')).toHaveCount(0);
		await expect(page.getByTestId('import-file-drop')).toBeVisible();

		const tenantCommitCsv = writeFixture(
			testInfo.outputDir,
			'tenant-import-valid.csv',
			[
				'firstName,lastName,email,phone',
				`${suffix} Final,Tenant,tenant.final.${Date.now()}@example.local,555-0102`
			].join('\n')
		);
		await uploadCsv(page, tenantCommitCsv);
		await expect(page.getByTestId('import-preview-summary')).toContainText('1 of 1 row looks good');
		await page.getByTestId('import-commit').click();
		await expect(page.getByTestId('import-result-summary')).toContainText('Created 1 tenant');

		await page.getByTestId('import-again').click();
		await expect(page.getByTestId('import-selected-file')).toHaveCount(0);
		await expect(page.getByTestId('import-result')).toHaveCount(0);
		await expect(page.getByTestId('import-file-drop')).toBeVisible();

		expect(consoleErrors).toEqual([]);
		expect(importRequests.some((r) => r.includes('dryRun=true'))).toBeTruthy();
		expect(importRequests.some((r) => r.includes('dryRun=false'))).toBeTruthy();
	});
});
