<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { accounting, type ChartOfAccountsRow } from '$lib/api/endpoints/accounting';
	import { EXPENSE_CATEGORY_OPTIONS, formatExpenseCategory } from '$lib/accounting/expense-categories';
	import { CAPABILITY } from '$lib/auth/experience-policy';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import { Button } from '$lib/components/ui/button';
	import * as Card from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import { hasCapability } from '$lib/stores/auth.svelte';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { ArrowLeft, CirclePlus, History, Pencil, ShieldCheck } from '@lucide/svelte';

	const ACCOUNT_TYPES = ['Asset', 'Liability', 'Equity', 'Income', 'Expense'] as const;
	type AccountType = (typeof ACCOUNT_TYPES)[number];
	const TYPE_START: Record<AccountType, number> = {
		Asset: 1000,
		Liability: 2000,
		Equity: 3000,
		Income: 4000,
		Expense: 5000
	};

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const canAdminister = $derived(hasCapability(CAPABILITY.moneyBalancesRead));
	const accountsQuery = createQuery(() => ({
		queryKey: ['chart-of-accounts', portfolioId, 'admin'],
		queryFn: () => accounting.chartOfAccounts({ activeOnly: false, take: 500, sort: 'code' }),
		enabled: canAdminister
	}));
	const activeAccountsQuery = createQuery(() => ({
		queryKey: ['chart-of-accounts', portfolioId, 'active'],
		queryFn: () => accounting.chartOfAccounts({ activeOnly: true, take: 500, sort: 'code' }),
		enabled: canAdminister
	}));

	let addOpen = $state(false);
	let editAccount = $state<ChartOfAccountsRow | null>(null);
	let addForm = $state({ code: '', name: '', accountType: 'Expense' as AccountType, parentAccountId: '', scheduleECategory: '' });
	let editForm = $state({ name: '', parentAccountId: '', scheduleECategory: '' });

	function suggestedCode(type: AccountType): string {
		const start = TYPE_START[type];
		const end = start + 999;
		let highest = start - 10;
		for (const account of accountsQuery.data?.items ?? []) {
			const code = Number(account.code);
			if (account.accountType === type && Number.isInteger(code) && code >= start && code <= end) highest = Math.max(highest, code);
		}
		return String(Math.min(Math.ceil((highest + 10) / 10) * 10, end));
	}

	function openAdd() {
		const accountType: AccountType = 'Expense';
		addForm = { code: suggestedCode(accountType), name: '', accountType, parentAccountId: '', scheduleECategory: '' };
		addOpen = true;
	}

	function changeAddType(accountType: AccountType) {
		addForm.accountType = accountType;
		addForm.code = suggestedCode(accountType);
		if (accountType !== 'Expense') addForm.scheduleECategory = '';
	}

	function openEdit(account: ChartOfAccountsRow) {
		editAccount = account;
		editForm = {
			name: account.name,
			parentAccountId: account.parentAccountId == null ? '' : String(account.parentAccountId),
			scheduleECategory: account.scheduleECategory ?? ''
		};
	}

	function refreshAccounts() {
		queryClient.invalidateQueries({ queryKey: ['chart-of-accounts', portfolioId] });
	}

	const createAccount = createMutation(() => ({
		mutationFn: () => accounting.createChartOfAccounts({
			code: addForm.code.trim(),
			name: addForm.name.trim(),
			accountType: addForm.accountType,
			normalBalance: addForm.accountType === 'Asset' || addForm.accountType === 'Expense' ? 'Debit' : 'Credit',
			parentAccountId: addForm.parentAccountId ? Number(addForm.parentAccountId) : null,
			scheduleECategory: addForm.scheduleECategory || null
		}),
		onSuccess: (account) => {
			refreshAccounts();
			addOpen = false;
			showSuccess(`${account.code} · ${account.name} was added.`);
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));

	const updateAccount = createMutation(() => ({
		mutationFn: () => accounting.patchChartOfAccounts(editAccount!.id, editAccount!.isSystem
			? { name: editForm.name.trim() }
			: {
				name: editForm.name.trim(),
				parentAccountId: editForm.parentAccountId ? Number(editForm.parentAccountId) : null,
				scheduleECategory: editForm.scheduleECategory || null
			}),
		onSuccess: (account) => {
			refreshAccounts();
			editAccount = null;
			showSuccess(`${account.code} · ${account.name} was updated.`);
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));

	const deactivateAccount = createMutation(() => ({
		mutationFn: (account: ChartOfAccountsRow) => accounting.patchChartOfAccounts(account.id, { isActive: false }),
		onSuccess: (account) => {
			refreshAccounts();
			showSuccess(`${account.code} · ${account.name} is inactive.`);
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));
</script>

<svelte:head><title>Chart of accounts · Rental Command</title></svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="chart-of-accounts-page">
	<PageHeader eyebrow="Money" title="Chart of accounts" description="Organize the accounts used by Money, expenses, and accounting reports." band tone="violet" art={6}>
		{#snippet actions()}
			<Button variant="outline" href="/accounting"><ArrowLeft class="h-4 w-4" /> Money</Button>
			{#if canAdminister}<Button onclick={openAdd} data-testid="add-account"><CirclePlus class="h-4 w-4" /> Add account</Button>{/if}
		{/snippet}
	</PageHeader>

	{#if !canAdminister}
		<Card.Root class="mt-6 border-destructive/40" data-testid="accounts-denied">
			<Card.Content class="p-5"><p class="font-medium">Chart of accounts is unavailable.</p><p class="mt-1 text-sm text-muted-foreground">Staff accounting access is required.</p></Card.Content>
		</Card.Root>
	{:else if accountsQuery.isLoading}
		<div class="mt-6"><LoadingState label="Loading chart of accounts" testid="accounts-loading" /></div>
	{:else if accountsQuery.isError}
		<Card.Root class="mt-6 border-destructive/40"><Card.Content class="flex items-center justify-between gap-4 p-5"><p>Could not load the chart of accounts.</p><Button variant="outline" onclick={() => accountsQuery.refetch()}>Retry</Button></Card.Content></Card.Root>
	{:else}
		<div class="mt-6 space-y-5">
			{#each accountsQuery.data?.items ?? [] as account, index (account.id)}
				{#if index === 0 || account.accountType !== accountsQuery.data?.items[index - 1]?.accountType}
					<h2 class="pt-2 text-lg font-semibold" data-testid="account-group-{account.accountType.toLowerCase()}">{account.accountType}</h2>
				{/if}
				<Card.Root class="m3-tonal-card m3-tonal-card--plain gap-0 py-0" data-testid="account-row-{account.id}">
					<Card.Content class="grid gap-4 p-4 md:grid-cols-[minmax(0,1fr)_minmax(12rem,0.55fr)_auto] md:items-center">
						<div class="min-w-0">
							<div class="flex flex-wrap items-center gap-2">
								<span class="font-mono text-sm font-semibold tabular-nums">{account.code}</span>
								<span class="font-semibold">{account.name}</span>
								{#if account.isSystem}<span class="rounded-full bg-[var(--m3c-primary-container)] px-2 py-0.5 text-xs font-medium text-[var(--m3c-on-primary-container)]"><ShieldCheck class="mr-1 inline h-3 w-3" />System</span>{/if}
								<span class="rounded-full px-2 py-0.5 text-xs font-medium {account.isActive ? 'bg-[var(--m3c-success-container)] text-[var(--m3c-on-success-container)]' : 'bg-[var(--m3c-surface-container-high)] text-muted-foreground'}">{account.isActive ? 'Active' : 'Inactive'}</span>
								{#if account.hasPostedLines}<span class="rounded-full bg-[var(--m3c-info-container)] px-2 py-0.5 text-xs font-medium text-[var(--m3c-on-info-container)]"><History class="mr-1 inline h-3 w-3" />Has history</span>{/if}
							</div>
							{#if account.isSystem}<p class="mt-1 text-xs text-muted-foreground">System accounts can be renamed, but cannot be deactivated or retyped because Money relies on them.</p>{:else if account.hasPostedLines}<p class="mt-1 text-xs text-muted-foreground">This account has posted history, so its type cannot be changed.</p>{/if}
						</div>
						<div><p class="text-xs font-medium text-muted-foreground">Schedule E mapping</p><p class="mt-1 text-sm">{account.scheduleECategory ? formatExpenseCategory(account.scheduleECategory) : 'Not mapped'}</p></div>
						<div class="flex flex-wrap justify-end gap-2">
							<Button variant="outline" size="sm" onclick={() => openEdit(account)} data-testid="rename-account-{account.id}"><Pencil class="h-4 w-4" /> Edit</Button>
							{#if !account.isSystem && account.isActive}<Button variant="outline" size="sm" onclick={() => deactivateAccount.mutate(account)} disabled={deactivateAccount.isPending} data-testid="deactivate-account-{account.id}">Deactivate</Button>{/if}
						</div>
					</Card.Content>
				</Card.Root>
			{/each}
		</div>
	{/if}
</div>

<Dialog.Root bind:open={addOpen}>
	<Dialog.Content class="sm:max-w-lg" data-testid="add-account-dialog">
		<Dialog.Header><Dialog.Title>Add account</Dialog.Title><Dialog.Description>The suggested code follows this account type's current range. You can change it.</Dialog.Description></Dialog.Header>
		<div class="grid gap-4 py-2 sm:grid-cols-2">
			<label class="grid gap-1.5 text-sm font-medium">Type<Select.Root type="single" value={addForm.accountType} onValueChange={(value) => value && changeAddType(value as AccountType)}><Select.Trigger class="w-full" data-testid="account-type">{addForm.accountType}</Select.Trigger><Select.Content>{#each ACCOUNT_TYPES as type}<Select.Item value={type} label={type}>{type}</Select.Item>{/each}</Select.Content></Select.Root></label>
			<label class="grid gap-1.5 text-sm font-medium">Code<Input bind:value={addForm.code} data-testid="account-code" /></label>
			<label class="grid gap-1.5 text-sm font-medium sm:col-span-2">Name<Input bind:value={addForm.name} data-testid="account-name" /></label>
			<label class="grid gap-1.5 text-sm font-medium">Parent account<Select.Root type="single" bind:value={addForm.parentAccountId}><Select.Trigger class="w-full">{activeAccountsQuery.data?.items.find((item) => String(item.id) === addForm.parentAccountId)?.name ?? 'No parent'}</Select.Trigger><Select.Content><Select.Item value="" label="No parent">No parent</Select.Item>{#each activeAccountsQuery.data?.items ?? [] as item (item.id)}<Select.Item value={String(item.id)} label={`${item.code} · ${item.name}`}>{item.code} · {item.name}</Select.Item>{/each}</Select.Content></Select.Root></label>
			<label class="grid gap-1.5 text-sm font-medium">Schedule E mapping<Select.Root type="single" bind:value={addForm.scheduleECategory}><Select.Trigger class="w-full">{addForm.scheduleECategory ? formatExpenseCategory(addForm.scheduleECategory) : 'Not mapped'}</Select.Trigger><Select.Content><Select.Item value="" label="Not mapped">Not mapped</Select.Item>{#each EXPENSE_CATEGORY_OPTIONS as option}<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>{/each}</Select.Content></Select.Root></label>
		</div>
		<Dialog.Footer><Button variant="outline" onclick={() => (addOpen = false)}>Cancel</Button><Button onclick={() => createAccount.mutate()} disabled={!addForm.code.trim() || !addForm.name.trim() || createAccount.isPending}>{createAccount.isPending ? 'Adding…' : 'Add account'}</Button></Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<Dialog.Root open={editAccount !== null} onOpenChange={(open) => { if (!open) editAccount = null; }}>
	<Dialog.Content class="sm:max-w-lg" data-testid="edit-account-dialog">
		<Dialog.Header><Dialog.Title>Edit {editAccount?.code}</Dialog.Title><Dialog.Description>{editAccount?.isSystem ? 'System accounts can be renamed only.' : 'Update the name, parent presentation, or Schedule E mapping. Posted account types stay fixed.'}</Dialog.Description></Dialog.Header>
		<div class="grid gap-4 py-2">
			<label class="grid gap-1.5 text-sm font-medium">Name<Input bind:value={editForm.name} data-testid="edit-account-name" /></label>
			{#if !editAccount?.isSystem}
				<label class="grid gap-1.5 text-sm font-medium">Parent account<Select.Root type="single" bind:value={editForm.parentAccountId}><Select.Trigger class="w-full">{activeAccountsQuery.data?.items.find((item) => String(item.id) === editForm.parentAccountId)?.name ?? 'No parent'}</Select.Trigger><Select.Content><Select.Item value="" label="No parent">No parent</Select.Item>{#each activeAccountsQuery.data?.items ?? [] as item (item.id)}{#if item.id !== editAccount?.id}<Select.Item value={String(item.id)} label={`${item.code} · ${item.name}`}>{item.code} · {item.name}</Select.Item>{/if}{/each}</Select.Content></Select.Root></label>
				<label class="grid gap-1.5 text-sm font-medium">Schedule E mapping<Select.Root type="single" bind:value={editForm.scheduleECategory}><Select.Trigger class="w-full">{editForm.scheduleECategory ? formatExpenseCategory(editForm.scheduleECategory) : 'Not mapped'}</Select.Trigger><Select.Content><Select.Item value="" label="Not mapped">Not mapped</Select.Item>{#each EXPENSE_CATEGORY_OPTIONS as option}<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>{/each}</Select.Content></Select.Root></label>
			{/if}
		</div>
		<Dialog.Footer><Button variant="outline" onclick={() => (editAccount = null)}>Cancel</Button><Button onclick={() => updateAccount.mutate()} disabled={!editForm.name.trim() || updateAccount.isPending}>{updateAccount.isPending ? 'Saving…' : 'Save changes'}</Button></Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
