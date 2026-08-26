/**
 * Unit Command Center — FUNCTIONAL e2e (not smoke).
 *
 * Where units.spec.ts only checks that pages/forms render, this file actually exercises the
 * inline domain work in each tab: it fills the relevant fields, saves, and proves the record PERSISTS
 * (re-queried out-of-band via the API) AND DISPLAYS (the saved values render back in the tab).
 * It also drives the NEGATIVE paths (missing/invalid → inline error visible) and the on-card
 * view↔edit flow. Runs against the LIVE local dev stack with the seeded admin account.
 *
 *   pnpm -C web test:e2e -- unit-functional.spec.ts
 *
 * A leased unit is discovered at runtime (findLeasedUnit) so the Rent tab is exercisable without
 * hard-coding an id. Created rows use a unique marker so re-runs don't collide and assertions can
 * pinpoint the new row regardless of how much seed data is already present.
 */

import { test, expect, type Page } from '@playwright/test';
import { login, apiToken, bearer, findLeasedUnit, unique } from './helpers';

/**
 * Pick a value from a bits-ui <Select> by its trigger testid. The trigger shows the current value
 * as its text; options render with role="option". We open, click the named option, and (best-effort)
 * wait for the trigger to reflect the chosen label.
 */
async function selectOption(page: Page, triggerTestId: string, optionLabel: string) {
	await page.getByTestId(triggerTestId).click();
	await page.getByRole('option', { name: optionLabel, exact: true }).click();
	await expect(page.getByTestId(triggerTestId)).toContainText(optionLabel);
}

/** Open a unit's Command Center directly on a given tab and wait for the tab panel. */
async function openUnitTab(page: Page, unitId: number, tab: string, panelTestId: string) {
	await page.goto(`/units/${unitId}?tab=${tab}`);
	await expect(page.getByTestId('unit-page')).toBeVisible({ timeout: 15_000 });
	await expect(page.getByTestId(panelTestId)).toBeVisible({ timeout: 15_000 });
	await page.waitForLoadState('networkidle');
}

/** Render a number the way the unit tabs do (USD currency), so display assertions match the UI. */
function asMoney(value: number): string {
	return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(value);
}

test.describe('Unit Command Center — functional', () => {
	// ─────────────────────────────────────────────────────────────────────────
	// All unit work tabs render their expected panel for a leased unit.
	// ─────────────────────────────────────────────────────────────────────────
	test('all unit tabs render for a leased unit', async ({ page, request }) => {
		const token = await apiToken(request);
		const { unit } = await findLeasedUnit(request, token);
		await login(page);

		const tabs: Array<[string, string, string]> = [
			['summary', 'tab-summary', 'unit-overview-tab'],
			['leasing', 'tab-leasing', 'unit-listing-tab'],
			['tenant-lease', 'tab-tenant-lease', 'unit-lease-tab'],
			['money', 'tab-money', 'unit-ledger-tab'],
			['maintenance', 'tab-maintenance', 'unit-maintenance-tab'],
			['documents-history', 'tab-documents-history', 'unit-documents-tab'],
		];

		await page.goto(`/units/${unit.id}`);
		await expect(page.getByTestId('unit-page')).toBeVisible({ timeout: 15_000 });
		for (const [, trigger, panel] of tabs) {
			await page.getByTestId(trigger).click();
			await expect(page.getByTestId(panel)).toBeVisible({ timeout: 10_000 });
		}
	});

	// ─────────────────────────────────────────────────────────────────────────
	// RENT — append-only receipt posting, validation, and exact entry detail.
	// ─────────────────────────────────────────────────────────────────────────
	test.describe('Rent tab', () => {
		test('records a receipt, persists, and displays it', async ({ page, request }) => {
			const token = await apiToken(request);
			const { unit, currentLease } = await findLeasedUnit(request, token);
			const tenantAccountId = currentLease!.tenantAccountId!;
			await login(page);
			await openUnitTab(page, unit.id, 'money&view=tenant-account', 'unit-rent-tab');

			// A distinctive sub-$1000 amount (no thousands comma) and description let us prove
			// the exact append-only ledger entry through both the page and detail read surfaces.
			const amount = 700 + (Date.now() % 1000) / 100; // e.g. 707.37
			const amountStr = amount.toFixed(2);
			const marker = unique('CC receipt');

			await page.getByRole('button', { name: 'Record payment', exact: true }).click();
			const form = page.getByTestId('record-payment-sheet');
			await expect(form).toBeVisible();
			await form.locator('label').filter({ hasText: 'Amount' }).locator('input').fill(amountStr);
			await form.locator('label').filter({ hasText: 'Note' }).locator('input').fill(marker);
			await form.locator('label').filter({ hasText: 'Reference' }).locator('input').fill(marker);
			await form.locator('label').filter({ hasText: 'Payment method' }).getByRole('button').click();
			await page.getByRole('option', { name: 'Check', exact: true }).click();
			await form.getByTestId('record-payment-submit').click();

			// Form closes on success; the immutable receipt row renders from the account ledger.
			await expect(page.getByTestId('record-payment-sheet')).toBeHidden();
			const receiptRow = page.locator('[data-testid^="tenant-ledger-row-"]').filter({ hasText: marker });
			await expect(receiptRow).toContainText(asMoney(amount), { timeout: 10_000 });

			// PERSISTENCE: the canonical global page returns this account-scoped receipt.
			const res = await request.get(
				`/api/v1/tenant-accounts/entries/page?tenantAccountId=${tenantAccountId}&entryType=PaymentReceipt&search=${encodeURIComponent(marker)}&take=20`,
				{
					headers: bearer(token),
				}
			);
			expect(res.ok()).toBeTruthy();
			const pageResult = (await res.json()) as {
				items: Array<{
					tenantAccountId: number;
					tenantLedgerEntryId: number;
					entryType: string;
					direction: string;
					amount: number;
					description: string;
				}>;
			};
			const match = pageResult.items.find((entry) => entry.description === marker);
			expect(match, `recorded receipt ${marker} not found in tenant-account ledger`).toBeTruthy();
			expect(match!.tenantAccountId).toBe(tenantAccountId);
			expect(match!.entryType).toBe('PaymentReceipt');
			expect(match!.direction).toBe('Credit');
			expect(Math.abs(match!.amount - amount) < 0.005).toBeTruthy();

			// The detail contract always carries both the account and entry identity.
			const detailRes = await request.get(
				`/api/v1/tenant-accounts/${tenantAccountId}/entries/${match!.tenantLedgerEntryId}`,
				{ headers: bearer(token) }
			);
			expect(detailRes.ok()).toBeTruthy();
			const detail = (await detailRes.json()) as {
				tenantAccountId: number;
				tenantLedgerEntryId: number;
				description: string;
				providerAttempt?: { paymentMethodSummary?: string; providerReference?: string };
			};
			expect(detail.tenantAccountId).toBe(tenantAccountId);
			expect(detail.tenantLedgerEntryId).toBe(match!.tenantLedgerEntryId);
			expect(detail.description).toBe(marker);
			expect(detail.providerAttempt?.paymentMethodSummary).toBe('Check');
			expect(detail.providerAttempt?.providerReference).toBe(marker);
		});

		test('rejects an empty amount with an inline error', async ({ page, request }) => {
			const token = await apiToken(request);
			const { unit } = await findLeasedUnit(request, token);
			await login(page);
			await openUnitTab(page, unit.id, 'money&view=tenant-account', 'unit-rent-tab');

			await page.getByRole('button', { name: 'Record payment', exact: true }).click();
			await expect(page.getByTestId('record-payment-sheet')).toBeVisible();
			// Amount defaults to '' already; submit straight away.
			await page
				.getByTestId('record-payment-sheet')
				.getByTestId('record-payment-submit')
				.click();

			await expect(page.getByText('Enter an amount greater than zero.', { exact: true })).toBeVisible();
			// Form stays open (no accidental save).
			await expect(page.getByTestId('record-payment-sheet')).toBeVisible();
		});

		test('rejects a zero amount with an inline error', async ({ page, request }) => {
			const token = await apiToken(request);
			const { unit } = await findLeasedUnit(request, token);
			await login(page);
			await openUnitTab(page, unit.id, 'money&view=tenant-account', 'unit-rent-tab');

			await page.getByRole('button', { name: 'Record payment', exact: true }).click();
			const form = page.getByTestId('record-payment-sheet');
			await form.locator('label').filter({ hasText: 'Amount' }).locator('input').fill('0');
			await form.getByTestId('record-payment-submit').click();

			await expect(page.getByText('Enter an amount greater than zero.', { exact: true })).toBeVisible();
			await expect(page.getByTestId('record-payment-sheet')).toBeVisible();
		});

		test('opens an immutable receipt through its exact account and entry ids', async ({ page, request }) => {
			const token = await apiToken(request);
			const { unit, currentLease } = await findLeasedUnit(request, token);
			const tenantAccountId = currentLease!.tenantAccountId!;
			await login(page);

			// Seed one append-only receipt through the canonical account command.
			const seedAmount = 1500 + (Date.now() % 90);
			const marker = unique('CC immutable receipt');
			const createRes = await request.post(`/api/v1/tenant-accounts/${tenantAccountId}/receipts`, {
				headers: { ...bearer(token), 'Idempotency-Key': unique('e2e-receipt') },
				data: {
					amount: seedAmount,
					effectiveOn: '2026-02-10',
					description: marker,
					paymentMethodSummary: 'Check',
					externalReference: marker,
					targetChargeEntryId: null,
				},
			});
			expect(createRes.ok(), `seed receipt failed: ${createRes.status()}`).toBeTruthy();
			const seeded = (await createRes.json()) as { value: { ledgerEntryId: number } };

			await openUnitTab(
				page,
				unit.id,
				`money&view=tenant-account&payment=${seeded.value.ledgerEntryId}`,
				'unit-rent-tab'
			);
			await expect(page.getByTestId('payment-detail-page')).toBeVisible({ timeout: 10_000 });
			await expect(page.getByTestId('payment-hero-amount')).toHaveText(asMoney(seedAmount));
			await expect(page.getByText(marker, { exact: true }).first()).toBeVisible();
			await expect(page.getByTestId('payment-detail-help-link')).toBeVisible();
			await expect(page.getByTestId('payment-detail-page').getByRole('button', { name: 'Edit', exact: true })).toHaveCount(0);
		});
	});

	// ─────────────────────────────────────────────────────────────────────────
	// MAINTENANCE — create work order (positive) + validation (negative).
	// ─────────────────────────────────────────────────────────────────────────
	test.describe('Maintenance tab', () => {
		test('creates a work order, persists, and displays it', async ({ page, request }) => {
			const token = await apiToken(request);
			const { unit } = await findLeasedUnit(request, token);
			await login(page);
			await openUnitTab(page, unit.id, 'maintenance', 'unit-maintenance-tab');

			const title = unique('CC Leaky Faucet');
			await page.getByTestId('maintenance-create').click();
			await expect(page.getByTestId('maintenance-create-form')).toBeVisible();
			await page.getByTestId('maintenance-title-input').fill(title);
			await page.getByTestId('maintenance-description-input').fill('Kitchen sink drips overnight.');
			await page.getByTestId('maintenance-create-next').click();
			await selectOption(page, 'maintenance-priority-input', 'High');
			await page.getByTestId('maintenance-category-input').fill('Plumbing');
			await page.getByTestId('maintenance-create-submit').click();

			await expect(page.getByTestId('maintenance-create-form')).toBeHidden();
			await expect(
				page.getByTestId('maintenance-work-orders').getByText(title, { exact: false })
			).toBeVisible({ timeout: 10_000 });

			// PERSISTENCE: the work order exists on this unit with the fields we set.
			const res = await request.get(`/api/v1/work-orders?unitId=${unit.id}&take=500`, {
				headers: bearer(token),
			});
			expect(res.ok()).toBeTruthy();
			const wos = (await res.json()) as Array<{
				title: string;
				description: string;
				priority: string;
				category: string;
				unitId?: number;
				propertyId: number;
				status: string;
			}>;
			const match = wos.find((w) => w.title === title);
			expect(match, `work order "${title}" not found in DB`).toBeTruthy();
			expect(match!.priority).toBe('High');
			expect(match!.category).toBe('Plumbing');
			expect(match!.description).toBe('Kitchen sink drips overnight.');
			expect(match!.unitId).toBe(unit.id);
			expect(match!.propertyId).toBe(unit.propertyId);
		});

		test('rejects a work order with no title/description', async ({ page, request }) => {
			const token = await apiToken(request);
			const { unit } = await findLeasedUnit(request, token);
			await login(page);
			await openUnitTab(page, unit.id, 'maintenance', 'unit-maintenance-tab');

			await page.getByTestId('maintenance-create').click();
			await expect(page.getByTestId('maintenance-create-form')).toBeVisible();
			// Submit with both required text fields blank.
			await page.getByTestId('maintenance-create-next').click();

			await expect(page.getByTestId('maintenance-title-error')).toBeVisible();
			await expect(page.getByTestId('maintenance-description-error')).toBeVisible();

			await page.getByTestId('maintenance-title-input').fill(unique('CC Fixed Faucet'));
			await page.getByTestId('maintenance-description-input').fill('Tenant reports a slow drip.');
			await expect(page.getByTestId('maintenance-title-error')).toBeHidden();
			await expect(page.getByTestId('maintenance-description-error')).toBeHidden();
			await expect(page.getByTestId('maintenance-create-form')).toBeVisible();
		});
	});

	// ─────────────────────────────────────────────────────────────────────────
	// EXPENSES — add expense (positive, must set UnitId), validation, edit.
	// ─────────────────────────────────────────────────────────────────────────
	test.describe('Expenses tab', () => {
		test('adds an expense with UnitId set, persists, and displays it', async ({ page, request }) => {
			const token = await apiToken(request);
			const { unit } = await findLeasedUnit(request, token);
			await login(page);
			await openUnitTab(page, unit.id, 'money&view=operating-costs', 'unit-expenses-tab');

			const desc = unique('CC Dishwasher repair');
			const amount = (320 + (Date.now() % 80)).toFixed(2);

			await page.getByTestId('expenses-create').click();
			await expect(page.getByTestId('expenses-create-form')).toBeVisible();
			await page.getByTestId('expenses-description-input').fill(desc);
			await page.getByTestId('expenses-amount-input').fill(amount);
			await page.getByTestId('expenses-create-next').click();
			await selectOption(page, 'expenses-category-input', 'Repairs & maintenance');
			await selectOption(page, 'expenses-status-input', 'Approved');
			const createdExpense = page.waitForResponse(
				(response) => response.url().endsWith('/api/v1/expenses') && response.request().method() === 'POST'
			);
			await page.getByTestId('expenses-create-submit').click();
			const createResponse = await createdExpense;
			expect(createResponse.ok()).toBeTruthy();
			const created = (await createResponse.json()) as { id: number };

			await expect(page.getByTestId('expenses-create-form')).toBeHidden();

			// PERSISTENCE + the critical UnitId assertion: read the expense back by its returned id.
			const res = await request.get(`/api/v1/expenses/${created.id}`, {
				headers: bearer(token),
			});
			expect(res.ok()).toBeTruthy();
			const match = (await res.json()) as {
				id: number;
				description: string;
				amount: number;
				category: string;
				status: string;
				unitId?: number;
				propertyId?: number;
			};
			expect(match.description).toBe(desc);
			expect(match.unitId, 'expense did not get UnitId set').toBe(unit.id);
			expect(match.propertyId).toBe(unit.propertyId);
			expect(match.category).toBe('Repairs');
			expect(match.status).toBe('Approved');
			expect(Math.abs(match.amount - Number(amount)) < 0.005).toBeTruthy();

			await page.goto(`/units/${unit.id}?tab=money&view=operating-costs&expense=${match.id}`);
			await expect(page.getByTestId('expense-detail-description-value')).toContainText(desc);
		});

		test('rejects an expense with no description and a zero amount', async ({ page, request }) => {
			const token = await apiToken(request);
			const { unit } = await findLeasedUnit(request, token);
			await login(page);
			await openUnitTab(page, unit.id, 'money&view=operating-costs', 'unit-expenses-tab');

			await page.getByTestId('expenses-create').click();
			await expect(page.getByTestId('expenses-create-form')).toBeVisible();
			await page.getByTestId('expenses-amount-input').fill('0'); // > 0 required
			await page.getByTestId('expenses-create-next').click();

			await expect(page.getByTestId('expenses-description-error')).toBeVisible();
			await expect(page.getByTestId('expenses-amount-error')).toBeVisible();

			await page.getByTestId('expenses-description-input').fill(unique('CC Corrected receipt'));
			await page.getByTestId('expenses-amount-input').fill('42.50');
			await expect(page.getByTestId('expenses-description-error')).toBeHidden();
			await expect(page.getByTestId('expenses-amount-error')).toBeHidden();
			await expect(page.getByTestId('expenses-create-form')).toBeVisible();
		});

		test('scan receipt opens the scan flow with unit context', async ({ page, request }) => {
			const token = await apiToken(request);
			const { unit } = await findLeasedUnit(request, token);
			await login(page);
			await openUnitTab(page, unit.id, 'money&view=operating-costs', 'unit-expenses-tab');

			await page.getByTestId('expenses-scan').click();
			await expect(page.getByTestId('scan-launcher-dialog')).toBeVisible({ timeout: 10_000 });
			await expect(page.getByRole('heading', { name: 'Scan an expense' })).toBeVisible();
			await expect(page.getByTestId('scan-context-source')).toContainText(`Unit ${unit.unitNumber}`);
			await expect(page).toHaveURL(`/units/${unit.id}?tab=money&view=operating-costs`);
		});

		test('edits an expense amount + category on the card', async ({ page, request }) => {
			const token = await apiToken(request);
			const { unit } = await findLeasedUnit(request, token);
			await login(page);

			// Seed an expense on the unit to edit.
			const seedDesc = unique('CC Seed expense');
			const createRes = await request.post('/api/v1/expenses', {
				headers: { ...bearer(token), 'Idempotency-Key': unique('e2e-expense') },
				data: {
					portfolioId: 1,
					unitId: unit.id,
					propertyId: unit.propertyId,
					description: seedDesc,
					amount: 100,
					incurredAt: '2026-01-05',
					category: 'Supplies',
					status: 'Pending',
				},
			});
			expect(createRes.ok(), `seed expense failed: ${createRes.status()}`).toBeTruthy();
			const seeded = (await createRes.json()) as { id: number };

			await openUnitTab(page, unit.id, 'money&view=operating-costs', 'unit-expenses-tab');

			const card = page.getByTestId(`expense-${seeded.id}`);
			await expect(card).toBeVisible({ timeout: 10_000 });
			await card.getByTestId(`expense-open-${seeded.id}`).click();
			const detail = page.getByTestId('expense-detail-page');
			await expect(detail).toBeVisible();
			await detail.getByRole('button', { name: 'Edit', exact: true }).click();

			// Change every editable field on the expense card.
			const newDesc = unique('CC Edited expense');
			await page.getByTestId('expense-detail-description-input').fill(newDesc);
			await page.getByTestId('expense-detail-amount-input').fill('275.50');
			await page.getByTestId('expense-detail-incurred-input').fill('2026-03-03');
			await selectOption(page, 'expense-detail-category-input', 'Cleaning & maintenance');
			await selectOption(page, 'expense-detail-status-input', 'Approved');
			await page.getByRole('button', { name: 'Save', exact: true }).click();

			// PERSISTENCE: API reflects every edited field.
			await expect(async () => {
				const r = await request.get(`/api/v1/expenses/${seeded.id}`, { headers: bearer(token) });
				expect(r.ok()).toBeTruthy();
				const e = (await r.json()) as {
					description: string;
					amount: number;
					category: string;
					status: string;
					incurredAt: string;
				};
				expect(e.description).toBe(newDesc);
				expect(Math.abs(e.amount - 275.5) < 0.005).toBeTruthy();
				expect(e.category).toBe('CleaningMaintenance');
				expect(e.status).toBe('Approved');
				expect(e.incurredAt.slice(0, 10)).toBe('2026-03-03');
			}).toPass({ timeout: 10_000 });

			// DISPLAY: the card's read view shows the new description + amount after refetch.
			await expect(page.getByTestId('expense-detail-description-value')).toContainText(newDesc, { timeout: 10_000 });
		});
	});

	// ─────────────────────────────────────────────────────────────────────────
	// LEASE — the tab renders the current lease's values; the on-card "Open lease"
	// link goes to the lease detail page where the lease is actually edited.
	// ─────────────────────────────────────────────────────────────────────────
	test.describe('Lease tab', () => {
		test('renders the current lease summary with real values', async ({ page, request }) => {
			const token = await apiToken(request);
			const dash = await findLeasedUnit(request, token);
			await login(page);
			await openUnitTab(page, dash.unit.id, 'tenant-lease&view=agreements', 'unit-lease-tab');

			await expect(page.getByTestId('unit-lease-tab').getByText(dash.currentLease!.leaseNumber)).toBeVisible();
		});

		test('opens the lease detail from the unit', async ({ page, request }) => {
			const token = await apiToken(request);
			const dash = await findLeasedUnit(request, token);
			const leaseManagementId = dash.currentLease!.leaseManagementId;
			await login(page);
			await openUnitTab(page, dash.unit.id, 'tenant-lease&view=agreements', 'unit-lease-tab');

			await page.getByTestId('unit-lease-tab').locator('a[href*="leaseManagement="]').first().click();
			await expect(page).toHaveURL(new RegExp(`leaseManagement=${leaseManagementId}`));
			await expect(page.getByText('Lease at a glance', { exact: true })).toBeVisible({ timeout: 15_000 });
		});
	});

	// ─────────────────────────────────────────────────────────────────────────
	// OVERVIEW / DOCUMENTS / TIMELINE — read-only tabs render their real DATA
	// (not just an empty panel), for a unit that actually has documents/history.
	// findLeasedUnit returns the first leased unit, which in the seed carries
	// payments, work orders, documents, and audit history.
	// ─────────────────────────────────────────────────────────────────────────
	test.describe('Read-only tabs render real data', () => {
		test('overview cards show snapshot, tenant/lease, rent, and repairs', async ({
			page,
			request,
		}) => {
			const token = await apiToken(request);
			const { unit } = await findLeasedUnit(request, token);
			await login(page);
			await openUnitTab(page, unit.id, 'summary', 'unit-overview-tab');

			await expect(page.getByTestId('overview-snapshot')).toBeVisible();
			await expect(page.getByTestId('overview-tenant-lease')).toBeVisible();
			await expect(page.getByTestId('overview-rent')).toBeVisible();
			await expect(page.getByTestId('overview-repairs')).toBeVisible();
			await expect(page.getByTestId('overview-docs')).toBeVisible();
			await expect(page.getByTestId('overview-appointments')).toBeVisible();
		});

		test('documents tab groups the unit documents on file', async ({ page, request }) => {
			const token = await apiToken(request);
			// Find a leased unit that also has documents (header.docsNeedingReviewCount > 0).
			const listRes = await request.get('/api/v1/units?take=500', { headers: bearer(token) });
			const units = (await listRes.json()) as Array<{ id: number }>;
			let target: number | null = null;
			for (const u of units) {
				const d = await request.get(`/api/v1/units/${u.id}/dashboard`, { headers: bearer(token) });
				if (!d.ok()) continue;
				const dash = (await d.json()) as {
					header?: { docsNeedingReviewCount?: number };
					overview?: { pendingDocs?: Array<unknown> };
				};
				if ((dash.overview?.pendingDocs?.length ?? 0) > 0) {
					target = u.id;
					break;
				}
			}
			test.skip(target == null, 'No unit with documents in the seed to verify grouping');
			await login(page);
			await openUnitTab(page, target!, 'documents-history&view=documents', 'unit-documents-tab');

			// At least one grouped document card renders (group testid is documents-group-<EntityType>).
			await expect(page.locator('[data-testid^="documents-group-"]').first()).toBeVisible({
				timeout: 10_000,
			});
		});

		test('timeline tab shows the unit history feed', async ({ page, request }) => {
			const token = await apiToken(request);
			const { unit } = await findLeasedUnit(request, token);
			// Confirm there IS history to show (otherwise the empty state is correct, not a bug).
			const tRes = await request.get(`/api/v1/units/${unit.id}/timeline?take=30&skip=0`, {
				headers: bearer(token),
			});
			const entries = (await tRes.json()) as Array<{ testId?: string }>;
			test.skip(entries.length === 0, 'No timeline history for this unit');

			await login(page);
			await openUnitTab(page, unit.id, 'documents-history&view=history', 'unit-timeline-tab');

			// The feed rendered at least one entry (not the "No history yet" empty state).
			await expect(page.getByTestId('unit-timeline-tab')).not.toContainText('No history yet');
			const firstTestId = entries.find((e) => e.testId)?.testId;
			if (firstTestId) {
				await expect(page.getByTestId('unit-timeline-tab').getByTestId(firstTestId).first()).toBeVisible(
					{ timeout: 10_000 }
				);
			}
		});
	});
});
