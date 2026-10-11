import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import type { ChartOfAccountsRow } from '$lib/api/endpoints/accounting-books';

import {
  ACCOUNT_DESTRUCTIVE_ACTIONS,
  buildCreateCategoryRequest,
  buildPatchCategoryRequest,
  canManageChartOfAccounts,
  categoryParentOptions,
  defaultCollapsedSections,
  getChartOfAccountsSections,
  toggleCollapsedSection,
  type CategoryDraft,
} from './chart-of-accounts-state.ts';

function account(overrides: Partial<ChartOfAccountsRow>): ChartOfAccountsRow {
  return {
    id: 1,
    publicId: 'acct-1',
    code: '4000',
    name: 'Account',
    accountType: 'Asset',
    normalBalance: 'Debit',
    parentAccountId: null,
    systemKey: null,
    scheduleECategory: null,
    isSystem: false,
    isActive: true,
    hasPostedLines: false,
    ...overrides,
  };
}

describe('chart of accounts state', () => {
  it('splits active categories, non-category system accounts, and inactive categories into sections', () => {
    const income = account({ id: 1, accountType: 'Income', name: 'Rent', isSystem: true });
    const expense = account({
      id: 2,
      accountType: 'Expense',
      name: 'Repairs',
      scheduleECategory: 'Repairs',
    });
    const systemCash = account({
      id: 3,
      accountType: 'Asset',
      name: 'Operating Cash',
      isSystem: true,
      systemKey: 'operating_cash',
    });
    const inactive = account({ id: 4, accountType: 'Expense', name: 'Old category', isActive: false });

    const sections = getChartOfAccountsSections([income, expense, systemCash, inactive]);

    assert.deepEqual(sections.map((section) => section.id), ['income', 'expense', 'system', 'inactive']);
    assert.deepEqual(sections.map((section) => section.rows.map((row) => row.id)), [[1], [2], [3], [4]]);
    assert.equal(sections[2]?.collapsedByDefault, true);
    assert.equal(sections[3]?.collapsedByDefault, true);
  });

  it('defaults system and inactive sections to collapsed and toggles immutably', () => {
    const initial = defaultCollapsedSections();
    const expanded = toggleCollapsedSection(initial, 'system');
    const restored = toggleCollapsedSection(expanded, 'system');

    assert.deepEqual([...initial], ['system', 'inactive']);
    assert.equal(initial.has('system'), true);
    assert.equal(expanded.has('system'), false);
    assert.equal(restored.has('system'), true);
  });

  it('builds the backend create contract without exposing accounting metadata as form fields', () => {
    const draft: CategoryDraft = {
      name: '  Landscaping  ',
      scheduleECategory: 'CleaningMaintenance',
      parentAccountId: 17,
    };

    assert.deepEqual(buildCreateCategoryRequest('Expense', draft), {
      name: 'Landscaping',
      categoryKind: 'expense',
      parentAccountId: 17,
      scheduleECategory: 'CleaningMaintenance',
      isActive: true,
    });
    assert.deepEqual(buildCreateCategoryRequest('Income', draft), {
      name: 'Landscaping',
      categoryKind: 'income',
      parentAccountId: 17,
      scheduleECategory: null,
      isActive: true,
    });
  });

  it('patches only editable category fields', () => {
    const patch = buildPatchCategoryRequest('Income', {
      name: 'Updated rent',
      scheduleECategory: 'Repairs',
      parentAccountId: null,
    });

    assert.deepEqual(patch, {
      name: 'Updated rent',
      parentAccountId: null,
      scheduleECategory: null,
    });
    assert.equal('code' in patch, false);
    assert.equal('accountType' in patch, false);
    assert.equal('normalBalance' in patch, false);
    assert.equal('systemKey' in patch, false);
  });

  it('offers active same-type parents but excludes the edited account', () => {
    const parent = account({ id: 10, accountType: 'Expense', name: 'Parent' });
    const edited = account({ id: 11, accountType: 'Expense', name: 'Edited' });
    const inactive = account({ id: 12, accountType: 'Expense', name: 'Inactive', isActive: false });
    const income = account({ id: 13, accountType: 'Income', name: 'Income' });

    assert.deepEqual(
      categoryParentOptions([parent, edited, inactive, income], 'Expense', edited.id).map((row) => row.id),
      [10],
    );
  });

  it('allows mutations only with the workspace administrator capability in Management', () => {
    const capabilities = new Set([ACCOUNT_DESTRUCTIVE_ACTIONS]);

    assert.equal(canManageChartOfAccounts('Management', capabilities), true);
    assert.equal(canManageChartOfAccounts('Management', new Set()), false);
    assert.equal(canManageChartOfAccounts('Tenant', capabilities), false);
  });
});
