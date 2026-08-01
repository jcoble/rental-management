<script lang="ts">
	import { page } from '$app/state';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { Plus } from '@lucide/svelte';

	import { accountingBooks } from '$lib/api/endpoints/accounting-books';
	import type { ChartOfAccountsRow } from '$lib/api/endpoints/accounting-books';
	import AccountingDetailMode from '$lib/components/accounting/AccountingDetailMode.svelte';
	import CategoryForm from '$lib/components/accounting/CategoryForm.svelte';
	import ChartOfAccountsTable from '$lib/components/accounting/ChartOfAccountsTable.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';
	import { Button } from '$lib/components/ui/button';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { capabilityKeysForExperience } from '$lib/types/user';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import {
		buildCreateCategoryRequest,
		buildPatchCategoryRequest,
		canManageChartOfAccounts,
		defaultCollapsedSections,
		toggleCollapsedSection,
		type CategoryDraft,
		type CategoryKind,
		type CategorySectionId
	} from '$lib/accounting/chart-of-accounts-state';

	const queryClient = useQueryClient();
	const authState = getAuthState();
	const currentAccess = $derived(page.data.access ?? authState.accessEnvelope ?? null);
	const activeExperience = $derived(authState.activeExperience ?? currentAccess?.selectedContext.activeExperience ?? null);
	const activeCapabilities = $derived(capabilityKeysForExperience(currentAccess, activeExperience));
	const canEdit = $derived(canManageChartOfAccounts(activeExperience, activeCapabilities));

	const accountsQuery = createQuery(() => ({
		queryKey: ['accounting-chart-of-accounts', 'all'],
		enabled: authState.isAuthenticated,
		queryFn: () => accountingBooks.chartOfAccounts({ activeOnly: false, take: 200, sort: 'code' })
	}));

	let collapsedSections = $state(defaultCollapsedSections());
	let formOpen = $state(false);
	let formKind = $state<CategoryKind>('Income');
	let editingAccount = $state<ChartOfAccountsRow | null>(null);
	let deactivationTarget = $state<ChartOfAccountsRow | null>(null);

	const accounts = $derived(accountsQuery.data?.items ?? []);

	type SaveInput = {
		account: ChartOfAccountsRow | null;
		kind: CategoryKind;
		draft: CategoryDraft;
	};

	const saveMutation = createMutation(() => ({
		mutationFn: ({ account, kind, draft }: SaveInput) =>
			account
				? accountingBooks.patchChartOfAccount(account.id, buildPatchCategoryRequest(kind, draft))
				: accountingBooks.createChartOfAccount(buildCreateCategoryRequest(kind, draft)),
		onSuccess: async (_result, input: SaveInput) => {
			formOpen = false;
			editingAccount = null;
			await invalidateChartOfAccounts();
			showSuccess(input.account ? 'Category updated.' : 'Category added.');
		},
		onError: (error) => showError(apiErrorMessage(error, 'Could not save this category.'))
	}));

	type ActivityInput = {
		account: ChartOfAccountsRow;
		isActive: boolean;
	};

	const activityMutation = createMutation(() => ({
		mutationFn: ({ account, isActive }: ActivityInput) =>
			accountingBooks.patchChartOfAccount(account.id, { isActive }),
		onSuccess: async (_result, input: ActivityInput) => {
			deactivationTarget = null;
			await invalidateChartOfAccounts();
			showSuccess(input.isActive ? 'Category activated.' : 'Category deactivated.');
		},
		onError: (error) => showError(apiErrorMessage(error, 'Could not update this category.'))
	}));

	async function invalidateChartOfAccounts(): Promise<void> {
		await queryClient.invalidateQueries({ queryKey: ['accounting-chart-of-accounts'] });
	}

	function openCreate(kind: CategoryKind): void {
		if (!canEdit) return;
		formKind = kind;
		editingAccount = null;
		formOpen = true;
	}

	function openEdit(account: ChartOfAccountsRow): void {
		if (!canEdit || account.isSystem || (account.accountType !== 'Income' && account.accountType !== 'Expense')) return;
		formKind = account.accountType;
		editingAccount = account;
		formOpen = true;
	}

	function closeForm(): void {
		if (saveMutation.isPending) return;
		formOpen = false;
		editingAccount = null;
	}

	function saveCategory(draft: CategoryDraft): void {
		if (!canEdit) return;
		saveMutation.mutate({ account: editingAccount, kind: formKind, draft });
	}

	function askToDeactivate(account: ChartOfAccountsRow): void {
		if (!canEdit || account.isSystem || !account.isActive) return;
		deactivationTarget = account;
	}

	function changeActivity(account: ChartOfAccountsRow, isActive: boolean): void {
		if (!canEdit || account.isSystem) return;
		activityMutation.mutate({ account, isActive });
	}

	function toggleSection(section: CategorySectionId): void {
		collapsedSections = toggleCollapsedSection(collapsedSections, section);
	}
</script>

<svelte:head>
	<title>Categories &amp; accounts - Rental Command</title>
</svelte:head>

<AccountingDetailMode class="mb-4" testid="chart-of-accounts-detail-mode">
	<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="chart-of-accounts-page">
		<PageHeader
			class="mb-6"
			band
			art={10}
			tone="violet"
			eyebrow="Money"
			title="Categories &amp; accounts"
			description="Organize the income and expense categories used across your workspace."
			data-testid="chart-of-accounts-header"
		>
			{#snippet actions()}
				{#if canEdit}
					<div class="flex flex-wrap gap-2">
						<Button variant="outline" class="gap-2" onclick={() => openCreate('Income')} data-testid="add-income-category">
							<Plus class="size-4" aria-hidden="true" /> Add income category
						</Button>
						<Button class="gap-2" onclick={() => openCreate('Expense')} data-testid="add-expense-category">
							<Plus class="size-4" aria-hidden="true" /> Add expense category
						</Button>
					</div>
				{/if}
			{/snippet}
		</PageHeader>

		{#if !canEdit}
			<div class="mb-4 rounded-xl border border-border bg-muted/30 px-4 py-3 text-sm text-muted-foreground" data-testid="chart-of-accounts-read-only-note">
				Categories and accounts are read-only for your role.
			</div>
		{/if}

		{#if accountsQuery.isError}
			<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-6" role="alert" data-testid="chart-of-accounts-error">
				<p class="font-medium text-destructive">Could not load categories and accounts.</p>
				<p class="mt-1 text-sm text-muted-foreground">Try again. The chart of accounts is temporarily unavailable.</p>
				<Button class="mt-4" variant="outline" onclick={() => accountsQuery.refetch()}>Try again</Button>
			</div>
		{:else if accountsQuery.isLoading}
			<LoadingState label="Loading categories and accounts" variant="section" testid="chart-of-accounts-loading" />
		{:else}
			<ChartOfAccountsTable
				{accounts}
				{canEdit}
				{collapsedSections}
				onedit={openEdit}
				ondeactivate={askToDeactivate}
				onactivate={(account) => changeActivity(account, true)}
				ontoggle={toggleSection}
			/>
		{/if}
	</div>
</AccountingDetailMode>

<CategoryForm
	open={formOpen}
	kind={formKind}
	account={editingAccount}
	{accounts}
	busy={saveMutation.isPending}
	onsave={saveCategory}
	oncancel={closeForm}
/>

<ConfirmDialog
	open={deactivationTarget !== null}
	title={`Deactivate ${deactivationTarget?.name ?? 'category'}?`}
	message="Existing records keep this category. It just can't be used on new records."
	confirmLabel="Deactivate category"
	busy={activityMutation.isPending}
	testid="deactivate-category"
	onconfirm={() => deactivationTarget && changeActivity(deactivationTarget, false)}
	oncancel={() => { if (!activityMutation.isPending) deactivationTarget = null; }}
/>
