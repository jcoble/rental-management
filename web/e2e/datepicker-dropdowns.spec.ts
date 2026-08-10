import { expect, test } from '@playwright/test';
import { loginWithApi } from './helpers';

type Rect = { x: number; y: number; width: number; height: number };

function gapBetween(a: Rect, b: Rect): number {
	return Math.max(a.y - (b.y + b.height), b.y - (a.y + a.height), 0);
}

function centerDistance(a: Rect, b: Rect): number {
	return Math.abs(a.x + a.width / 2 - (b.x + b.width / 2));
}

function nearestVerticalGap(a: Rect, b: Rect): number {
	return Math.min(Math.abs(a.y - (b.y + b.height)), Math.abs(b.y - (a.y + a.height)));
}

test.describe('TSK-846 calendar and range dropdowns', () => {
	test.use({ viewport: { width: 1710, height: 990 } });

	test('month and year menus stay anchored with shared Select styling and preserve selection', async ({ page, request }) => {
		await loginWithApi(page, request);
		await page.goto('/accounting?tab=activity', { waitUntil: 'networkidle' });
		await expect(page.getByTestId('accounting-tab-activity')).toHaveAttribute('aria-selected', 'true');
		await expect(page.evaluate(() => `${window.innerWidth}x${window.innerHeight}`)).resolves.toBe('1710x990');

		await page.getByTestId('transaction-date-range-filter').click();
		const monthTrigger = page.getByRole('button', { name: 'Choose month' }).first();
		await expect(monthTrigger).not.toHaveClass(/border-0/);
		await expect(monthTrigger).not.toHaveClass(/bg-transparent/);
		await monthTrigger.click();
		const monthContent = page.locator('[data-slot="select-content"]:visible').last();
		await expect(monthContent).toBeVisible();
		await expect(monthContent).toHaveClass(/bg-popover/);
		await expect(monthContent).toHaveClass(/border/);
		await expect(monthContent).toHaveClass(/shadow-md/);
		await expect(monthContent.locator('[data-slot="select-item"][aria-selected="true"] svg')).toHaveCount(1);
		const monthTriggerRect = (await monthTrigger.boundingBox()) as Rect;
		const monthContentRect = (await monthContent.boundingBox()) as Rect;
		expect(centerDistance(monthContentRect, monthTriggerRect)).toBeLessThanOrEqual(12);
		expect(gapBetween(monthContentRect, monthTriggerRect)).toBeLessThanOrEqual(8);
		expect(nearestVerticalGap(monthContentRect, monthTriggerRect)).toBeLessThanOrEqual(8);
		expect(monthContentRect.x).toBeGreaterThanOrEqual(0);
		expect(monthContentRect.y).toBeGreaterThanOrEqual(0);
		expect(monthContentRect.x + monthContentRect.width).toBeLessThanOrEqual(1710);
		expect(monthContentRect.y + monthContentRect.height).toBeLessThanOrEqual(990);
		await monthContent.getByRole('option', { name: 'Jan', exact: true }).click();
		await expect(monthTrigger).toContainText('Jan');

		const yearTrigger = page.getByRole('button', { name: 'Choose year' }).first();
		await yearTrigger.click();
		const yearContent = page.locator('[data-slot="select-content"]:visible').last();
		await expect(yearContent).toBeVisible();
		await expect(yearContent).toHaveClass(/bg-popover/);
		await expect(yearContent).toHaveClass(/border/);
		await expect(yearContent).toHaveClass(/shadow-md/);
		await expect(yearContent.locator('[data-slot="select-item"][aria-selected="true"] svg')).toHaveCount(1);
		const yearTriggerRect = (await yearTrigger.boundingBox()) as Rect;
		const yearContentRect = (await yearContent.boundingBox()) as Rect;
		expect(centerDistance(yearContentRect, yearTriggerRect)).toBeLessThanOrEqual(12);
		expect(nearestVerticalGap(yearContentRect, yearTriggerRect)).toBeLessThanOrEqual(8);
		expect(yearContentRect.x).toBeGreaterThanOrEqual(0);
		expect(yearContentRect.y).toBeGreaterThanOrEqual(0);
		expect(yearContentRect.x + yearContentRect.width).toBeLessThanOrEqual(1710);
		expect(yearContentRect.y + yearContentRect.height).toBeLessThanOrEqual(990);
		await yearContent.getByRole('option', { name: '2025', exact: true }).click();
		await expect(yearTrigger).toContainText('2025');
	});

	test('one range picker renders both year menus and a connected multi-week band', async ({ page, request }) => {
		await loginWithApi(page, request);
		await page.goto('/reports/rent-ledger', { waitUntil: 'networkidle' });
		await expect(page.evaluate(() => `${window.innerWidth}x${window.innerHeight}`)).resolves.toBe('1710x990');

		await page.getByTestId('report-date-range').click();
		const yearTriggers = page.getByRole('button', { name: 'Choose year' });
		await expect(yearTriggers).toHaveCount(2);
		for (let index = 0; index < 2; index += 1) {
			await yearTriggers.nth(index).click();
			const content = page.locator('[data-slot="select-content"]:visible').last();
			expect(await content.getByRole('option').count()).toBeGreaterThan(100);
			await expect(content.getByRole('option', { name: '2026', exact: true })).toBeVisible();
			await page.keyboard.press('Escape');
		}

		await page.getByTestId('report-date-range-start-input').fill('08/03/2026');
		await page.getByTestId('report-date-range-end-input').fill('09/17/2026');
		await expect(page.getByTestId('report-date-range')).toContainText('Aug 3, 2026 – Sep 17, 2026');
		await page.getByTestId('report-date-range-start-input').fill('');
		await page.getByTestId('report-date-range-end-input').fill('');

		const values = await page.locator('[data-range-calendar-day][data-value]:not([data-outside-month])').evaluateAll((nodes) =>
			nodes.map((node) => (node as HTMLElement).dataset.value).filter((value): value is string => Boolean(value))
		);
		const months = [...new Set(values.map((value) => value.slice(0, 7)))];
		expect(months.length).toBeGreaterThanOrEqual(2);
		const start = values.filter((value) => value.startsWith(months[0]))[2];
		const end = values.filter((value) => value.startsWith(months[1]))[15];
		expect(start).toBeTruthy();
		expect(end).toBeTruthy();
		await page.locator(`[data-range-calendar-day][data-value="${start}"]:not([data-outside-month])`).first().click();
		await page.locator(`[data-range-calendar-day][data-value="${end}"]:not([data-outside-month])`).first().click();
		await expect(page.getByTestId('report-date-range')).toContainText('–');

		await page.getByTestId('report-date-range').click();
		await expect(page.locator('[data-range-start]')).toHaveCount(2);
		await expect(page.locator('[data-range-end]')).toHaveCount(2);
		expect(await page.locator('[data-range-middle]').count()).toBeGreaterThan(0);
		await page.screenshot({ path: 'output/playwright/tsk846-after-range-connected.png', fullPage: false });
	});
});
