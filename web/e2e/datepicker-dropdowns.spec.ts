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

		const primaryMonthDays = page.locator('[data-range-calendar-day][data-value]:visible:not([data-outside-month])');
		const values = await primaryMonthDays.evaluateAll((nodes) =>
			nodes.map((node) => (node as HTMLElement).dataset.value).filter((value): value is string => Boolean(value))
		);
		const months = [...new Set(values.map((value) => value.slice(0, 7)))];
		expect(months.length).toBeGreaterThanOrEqual(2);
		const start = `${months[0]}-10`;
		const end = `${months[1]}-10`;
		expect(values).toContain(start);
		expect(values).toContain(end);
		await page.locator(`[data-range-calendar-day][data-value="${start}"]:visible:not([data-outside-month])`).click();
		await page.locator(`[data-range-calendar-day][data-value="${end}"]:visible:not([data-outside-month])`).click();
		await expect(page.getByTestId('report-date-range')).toContainText('–');

		await page.getByTestId('report-date-range').click();
		const selectedStart = page.locator(`[data-range-calendar-day][data-value="${start}"]:visible:not([data-outside-month])`);
		const selectedEnd = page.locator(`[data-range-calendar-day][data-value="${end}"]:visible:not([data-outside-month])`);
		await expect(selectedStart).toHaveAttribute('data-range-start', '');
		await expect(selectedEnd).toHaveAttribute('data-range-end', '');
		const interiorValues = values.filter((value) => value > start && value < end);
		expect(interiorValues.length).toBeGreaterThan(20);
		for (const value of interiorValues) {
			await expect(
				page.locator(`[data-range-calendar-day][data-value="${value}"]:visible:not([data-outside-month])`)
			).toHaveAttribute('data-range-middle', '');
		}
		const interiorMissingMiddle = await primaryMonthDays.evaluateAll((nodes, range) => {
			const { start: rangeStart, end: rangeEnd } = range as { start: string; end: string };
			return nodes
				.filter((node) => {
					const value = (node as HTMLElement).dataset.value;
					return Boolean(value && value > rangeStart && value < rangeEnd);
				})
				.filter((node) => !(node as HTMLElement).hasAttribute('data-range-middle'))
				.map((node) => (node as HTMLElement).dataset.value)
				.filter((value): value is string => Boolean(value));
		}, { start, end });
		expect(interiorMissingMiddle).toEqual([]);
		await page.screenshot({ path: 'output/playwright/tsk846-after-range-connected.png', fullPage: false });
	});

	test('accounting Activity clears its range on a phone and preserves exact ISO request params', async ({ page, request }) => {
		await page.setViewportSize({ width: 390, height: 640 });
		await expect(page.evaluate(() => `${window.innerWidth}x${window.innerHeight}`)).resolves.toBe('390x640');
		await loginWithApi(page, request);

		const initialFrom = '2026-08-03';
		const initialTo = '2026-09-17';
		const transactionRequests: URL[] = [];
		page.on('request', (request) => {
			const url = new URL(request.url());
			if (url.pathname.endsWith('/api/v1/accounting/transactions')) transactionRequests.push(url);
		});

		await page.goto(`/accounting?tab=activity&from=${initialFrom}&to=${initialTo}`, { waitUntil: 'networkidle' });
		await expect(page.getByTestId('accounting-tab-activity')).toHaveAttribute('aria-selected', 'true');
		await expect(page.getByTestId('transaction-date-range-filter')).toContainText('Aug 3, 2026 – Sep 17, 2026');
		await expect.poll(() => transactionRequests.some((url) => url.searchParams.get('from') === initialFrom && url.searchParams.get('to') === initialTo)).toBe(true);

		const transactionRequestCountBeforeClear = transactionRequests.length;
		const clearRangeButton = page.getByRole('button', { name: 'Clear range', exact: true });
		await expect(clearRangeButton).toBeVisible();
		await clearRangeButton.click();
		await expect.poll(() => transactionRequests.length).toBeGreaterThan(transactionRequestCountBeforeClear);
		const firstTransactionRequestAfterClear = transactionRequests[transactionRequestCountBeforeClear];
		expect(firstTransactionRequestAfterClear).toBeDefined();
		expect(firstTransactionRequestAfterClear?.searchParams.has('from')).toBe(false);
		expect(firstTransactionRequestAfterClear?.searchParams.has('to')).toBe(false);
		await expect(page.getByTestId('transaction-date-range-filter')).toContainText('Transaction date range');
		await expect.poll(() => {
			const url = new URL(page.url());
			return !url.searchParams.has('from') && !url.searchParams.has('to');
		}).toBe(true);
		await page.screenshot({ path: 'output/playwright/tsk846-round2-accounting-mobile-cleared.png', fullPage: false });

		await page.getByTestId('transaction-date-range-filter').click();
		const visibleDays = page.locator('[data-range-calendar-day][data-value]:visible:not([data-outside-month])');
		await expect(visibleDays).not.toHaveCount(0);
		const newStart = '2026-08-05';
		const newEnd = '2026-08-12';
		await page.locator(`[data-range-calendar-day][data-value="${newStart}"]:visible:not([data-outside-month])`).click();
		await page.locator(`[data-range-calendar-day][data-value="${newEnd}"]:visible:not([data-outside-month])`).click();
		await expect.poll(() => transactionRequests.some((url) => url.searchParams.get('from') === newStart && url.searchParams.get('to') === newEnd)).toBe(true);
		await page.screenshot({ path: 'output/playwright/tsk846-round2-accounting-mobile-new-range.png', fullPage: false });
	});
});
