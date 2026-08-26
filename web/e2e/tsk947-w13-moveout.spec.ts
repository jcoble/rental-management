import { expect, test } from '@playwright/test';
import { apiToken, bearer, findLeasedUnit, login } from './helpers';

/**
 * TSK-947 W13 — the two real-world move-out moments.
 *
 * "Plan move-out" is one short screen: what is happening, the move-out date, an
 * optional notice date, and why. "Keys returned" is a single confirmation whose
 * per-person and per-sign-in choices stay folded away under a disclosure. The
 * Turnover tab then carries the close-out checklist, and the Close account button
 * stays disabled until every row on it is green.
 */
test.describe('TSK-947 W13 — plan move-out and keys returned', () => {
	test('shows both move-out screens in plain words and gates closing on the checklist', async ({
		page,
		request
	}) => {
		const token = await apiToken(request);
		const { unit, currentLease } = await findLeasedUnit(request, token);
		const leaseManagementId = currentLease?.leaseManagementId;
		expect(leaseManagementId, 'The leased unit has no lease to move out of').toBeTruthy();

		// The move-out actions only exist once the tenant actually has the keys.
		const leasesRes = await request.get(
			`/api/v1/lease-managements/page?unitId=${unit.id}&take=50&sort=-updatedAtUtc`,
			{ headers: bearer(token) }
		);
		expect(leasesRes.ok(), `lease-management page failed: ${leasesRes.status()}`).toBeTruthy();
		const leases = (await leasesRes.json()) as {
			items: Array<{
				leaseManagementId: number;
				possessionGivenAtUtc?: string | null;
				possessionReturnedAtUtc?: string | null;
			}>;
		};
		const occupied = leases.items.find(
			(item) => item.possessionGivenAtUtc && !item.possessionReturnedAtUtc
		);
		test.skip(!occupied, 'No unit in the sample data has a tenant holding the keys');

		await login(page);
		await page.goto(`/units/${unit.id}?tab=tenant-lease&view=agreements`, {
			waitUntil: 'domcontentloaded'
		});
		await expect(page.getByTestId('unit-lease-tab')).toBeVisible({ timeout: 15_000 });

		// ── 1. Plan move-out: four things and nothing else. ──────────────────────
		await page.getByTestId(`lease-plan-move-out-${occupied!.leaseManagementId}`).click();
		const plan = page.getByTestId('plan-move-out-dialog');
		await expect(plan).toBeVisible();
		await expect(plan).toContainText('Plan move-out');
		await expect(page.getByTestId('plan-move-out-what')).toBeVisible();
		await expect(page.getByTestId('plan-move-out-reason')).toBeVisible();

		// The dates only matter when someone is actually leaving.
		await page.getByTestId('plan-move-out-what').click();
		await page.getByTestId('plan-move-out-what-option-Undecided').click();
		await expect(page.getByTestId('plan-move-out-date')).toHaveCount(0);
		await page.getByTestId('plan-move-out-what').click();
		await page.getByTestId('plan-move-out-what-option-NonRenewalMoveOut').click();
		await expect(page.getByTestId('plan-move-out-what')).toContainText('Tenant is leaving');
		await expect(page.getByTestId('plan-move-out-date')).toBeVisible();
		await expect(page.getByTestId('plan-move-out-notice')).toBeVisible();
		await expect(plan).toContainText('(optional)');

		// One screen, not a wizard, and no accountant words on it.
		await expect(plan.getByRole('button', { name: 'Save' })).toBeVisible();
		await expect(plan).not.toContainText(/disposition|possession|relationship/i);

		// Happy path: a move-out date, no notice date, a reason, save.
		await page.getByTestId('plan-move-out-date').fill('12/31/2026');
		await page.getByTestId('plan-move-out-reason').fill('Gave notice, moving closer to work.');
		await page.getByTestId('plan-move-out-save').click();
		await expect(plan).toBeHidden({ timeout: 15_000 });

		// ── 2. Keys returned: one line, the rest folded away. ────────────────────
		await page.getByTestId('lease-return-possession').first().click();
		const keys = page.getByTestId('lease-return-possession-dialog');
		await expect(keys).toBeVisible();
		await expect(page.getByTestId('moveout-summary-line')).toContainText(
			'Everyone on this lease moves out and their app access ends.'
		);

		const changes = page.getByTestId('moveout-access-changes');
		await expect(changes).toHaveJSProperty('open', false);
		await expect(page.getByTestId('return-turnover-reason')).toBeHidden();
		await expect(page.getByTestId('return-possession-submit')).toContainText(
			'Record keys returned'
		);

		await changes.getByText('Change what happens to their access').click();
		await expect(changes).toHaveJSProperty('open', true);
		await expect(page.getByTestId('return-turnover-reason')).toBeVisible();
		await expect(keys).not.toContainText(/disposition|possession|relationship/i);

		// Leave the tenant in place — the move-out itself is not what this spec proves.
		await keys.getByRole('button', { name: 'Cancel' }).click();
		await expect(keys).toBeHidden();

		// ── 3. Turnover: the close-out checklist gates the Close account button. ─
		await page.goto(`/units/${unit.id}?tab=maintenance&view=turnover`, {
			waitUntil: 'domcontentloaded'
		});
		await expect(page.getByTestId('unit-turnover-tab')).toBeVisible({ timeout: 15_000 });

		const checklist = page.getByTestId('closeout-checklist');
		await expect(checklist).toBeVisible();
		await expect(page.getByTestId('closeout-row-keys')).toContainText('Keys are back');
		await expect(page.getByTestId('closeout-row-deposit')).toBeVisible();
		await expect(page.getByTestId('closeout-row-balance')).toBeVisible();
		await expect(page.getByTestId('closeout-row-paperwork')).toBeVisible();
		await expect(checklist).not.toContainText(/disposition|possession|relationship/i);

		// The keys are still with the tenant, so the account cannot close yet.
		await expect(page.getByTestId('closeout-close-account')).toBeDisabled();
	});
});
