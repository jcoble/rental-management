import type {
  AccountType,
  ChartOfAccountsRow,
  CreateChartOfAccountsRequest,
  NormalBalance,
  PatchChartOfAccountsRequest,
  ScheduleECategory,
} from '$lib/api/endpoints/accounting-books';

export const ACCOUNT_DESTRUCTIVE_ACTIONS = 'account.destructive-actions' as const;

export type CategoryKind = Extract<AccountType, 'Income' | 'Expense'>;
export type CategorySectionId = 'income' | 'expense' | 'system' | 'inactive';

export interface CategoryDraft {
  name: string;
  scheduleECategory: ScheduleECategory | null;
  parentAccountId: number | null;
}

export interface ChartOfAccountsSection {
  id: CategorySectionId;
  label: string;
  rows: ChartOfAccountsRow[];
  collapsedByDefault: boolean;
  emptyLabel: string;
}

export const DEFAULT_COLLAPSED_SECTIONS: readonly CategorySectionId[] = ['system', 'inactive'];

export function defaultCollapsedSections(): Set<CategorySectionId> {
  return new Set(DEFAULT_COLLAPSED_SECTIONS);
}

export function toggleCollapsedSection(
  collapsed: ReadonlySet<CategorySectionId>,
  section: CategorySectionId,
): Set<CategorySectionId> {
  const next = new Set(collapsed);

  if (next.has(section)) {
    next.delete(section);
  } else {
    next.add(section);
  }

  return next;
}

export function getChartOfAccountsSections(
  accounts: readonly ChartOfAccountsRow[],
): ChartOfAccountsSection[] {
  return [
    {
      id: 'income',
      label: 'Income categories',
      rows: accounts.filter((account) => account.isActive && account.accountType === 'Income'),
      collapsedByDefault: false,
      emptyLabel: 'No income categories yet.',
    },
    {
      id: 'expense',
      label: 'Expense categories',
      rows: accounts.filter((account) => account.isActive && account.accountType === 'Expense'),
      collapsedByDefault: false,
      emptyLabel: 'No expense categories yet.',
    },
    {
      id: 'system',
      label: 'System accounts',
      rows: accounts.filter((account) => account.isActive && account.isSystem && !isCategory(account)),
      collapsedByDefault: true,
      emptyLabel: 'No system accounts.',
    },
    {
      id: 'inactive',
      label: 'Inactive categories',
      rows: accounts.filter((account) => !account.isActive && !account.isSystem),
      collapsedByDefault: true,
      emptyLabel: 'No inactive categories.',
    },
  ];
}

export function categoryParentOptions(
  accounts: readonly ChartOfAccountsRow[],
  kind: CategoryKind,
  editingAccountId: number | null = null,
): ChartOfAccountsRow[] {
  return accounts.filter(
    (account) =>
      account.isActive &&
      account.accountType === kind &&
      account.id !== editingAccountId,
  );
}

export function buildCreateCategoryRequest(
  kind: CategoryKind,
  draft: CategoryDraft,
): CreateChartOfAccountsRequest {
  const normalBalance: NormalBalance = kind === 'Expense' ? 'Debit' : 'Credit';

  return {
    code: '',
    name: draft.name.trim(),
    accountType: kind,
    normalBalance,
    parentAccountId: draft.parentAccountId,
    systemKey: null,
    scheduleECategory: kind === 'Expense' ? draft.scheduleECategory : null,
    isActive: true,
  };
}

export function buildPatchCategoryRequest(
  kind: CategoryKind,
  draft: CategoryDraft,
): PatchChartOfAccountsRequest {
  return {
    name: draft.name.trim(),
    parentAccountId: draft.parentAccountId,
    scheduleECategory: kind === 'Expense' ? draft.scheduleECategory : null,
  };
}

export function canManageChartOfAccounts(
  activeExperience: string | null | undefined,
  capabilities: ReadonlySet<string>,
): boolean {
  return activeExperience === 'Management' && capabilities.has(ACCOUNT_DESTRUCTIVE_ACTIONS);
}

function isCategory(account: ChartOfAccountsRow): boolean {
  return account.accountType === 'Income' || account.accountType === 'Expense';
}
