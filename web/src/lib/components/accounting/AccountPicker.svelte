<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import * as Combobox from '$lib/components/ui/combobox';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { debounced } from '$lib/utils/debounce.svelte';
	import {
		formatAccountPickerLabel,
		formatAccountTypeLabel,
		formatSimpleAccountGroupLabel
	} from '$lib/accounting/accounting-display';
	import {
		accountingBooks,
		type AccountType,
		type ChartOfAccountsRow
	} from '$lib/api/endpoints/accounting-books';
	import { getAccountingDetailMode } from './AccountingDetailMode.svelte';

	const ALL_ACCOUNTS = '__all_accounts__';
	const GROUP_ORDER = ['Income', 'Expenses', 'Cash & bank', 'Money owed to you', 'Money you owe', 'Equity'];

	let {
		selectedAccountId = $bindable<number | null>(null),
		allowAll = false,
		types,
		onchange
	}: {
		selectedAccountId?: number | null;
		allowAll: boolean;
		types?: AccountType[];
		onchange?: (accountId: number | null) => void;
	} = $props();

	const authState = getAuthState();
	const detailMode = getAccountingDetailMode();
	const mode = $derived(detailMode?.mode ?? 'simple');
	const advanced = $derived(mode === 'advanced');
	let search = $state('');
	let comboboxValue = $state('');
	const debouncedSearch = debounced(() => search, 250);

	const accountsQuery = createQuery(() => ({
		queryKey: ['accounting-chart-of-accounts', 'active', debouncedSearch.value],
		enabled: authState.isAuthenticated,
		queryFn: () => accountingBooks.chartOfAccounts({ activeOnly: true, search: debouncedSearch.value.trim() || undefined })
	}));

	function errorStatus(error: unknown): number | undefined {
		if (!error || typeof error !== 'object' || !('status' in error)) return undefined;
		const status = (error as { status?: unknown }).status;
		return typeof status === 'number' ? status : undefined;
	}

	const unauthorized = $derived(
		!authState.isAuthenticated || [401, 403, 404].includes(errorStatus(accountsQuery.error) ?? 0)
	);
	const accounts = $derived.by(() => {
		const permittedTypes = types?.length ? new Set(types) : null;
		return (accountsQuery.data?.items ?? []).filter(
			(account) => !permittedTypes || permittedTypes.has(account.accountType)
		);
	});
	const filteredAccounts = $derived(accounts);

	const groups = $derived.by(() => {
		const grouped = new Map<string, ChartOfAccountsRow[]>();
		for (const account of filteredAccounts) {
			const group = formatSimpleAccountGroupLabel(account);
			const existing = grouped.get(group);
			if (existing) existing.push(account);
			else grouped.set(group, [account]);
		}
		return GROUP_ORDER.filter((label) => grouped.has(label)).map((label) => ({
			label,
			accounts: grouped.get(label) ?? []
		}));
	});

	const comboItems = $derived([
		...(allowAll ? [{ value: ALL_ACCOUNTS, label: 'All categories and accounts' }] : []),
		...filteredAccounts.map((account) => ({
			value: String(account.id),
			label: formatAccountPickerLabel(account, advanced)
		}))
	]);

	$effect(() => {
		const next = selectedAccountId == null ? (allowAll ? ALL_ACCOUNTS : '') : String(selectedAccountId);
		if (comboboxValue !== next) comboboxValue = next;
	});

	function choose(value: string | undefined): void {
		if (!value) return;
		const next = value === ALL_ACCOUNTS ? null : Number(value);
		if (next !== null && !Number.isInteger(next)) return;
		selectedAccountId = next;
		onchange?.(next);
		search = '';
	}
</script>

{#if !unauthorized}
	<div class="min-w-0" data-testid="account-picker">
		{#if accountsQuery.isLoading}
			<div class="h-10 w-full animate-pulse rounded-md border border-border bg-muted" role="status" aria-label="Loading categories and accounts"></div>
		{:else if accountsQuery.isError}
			<div class="rounded-md border border-border px-3 py-2 text-sm text-muted-foreground">
				Categories and accounts are unavailable.
			</div>
		{:else}
			<Combobox.Root
				type="single"
				bind:value={comboboxValue}
				bind:inputValue={search}
				items={comboItems}
				allowDeselect={false}
				onValueChange={(value) => choose(typeof value === 'string' ? value : value?.[0])}
			>
				<Combobox.Input
					placeholder={allowAll ? 'All categories and accounts' : 'Choose a category or account'}
					aria-label="Choose a category or account"
				/>
				<Combobox.Content class="min-w-[18rem]">
					{#if comboItems.length === 0}
						<div class="px-3 py-6 text-center text-sm text-muted-foreground">No matching categories or accounts.</div>
					{:else}
						{#each groups as group (group.label)}
							<div class="px-2 pb-1 pt-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground">{group.label}</div>
							{#each group.accounts as account (account.id)}
								<Combobox.Item
									value={String(account.id)}
									label={formatAccountPickerLabel(account, advanced)}
								>
									<span class="flex min-w-0 flex-col">
										<span class="truncate">{account.name}</span>
										{#if advanced}<span class="text-xs opacity-75">{account.code} · {formatAccountTypeLabel(account.accountType)}</span>{/if}
									</span>
								</Combobox.Item>
							{/each}
						{/each}
						{#if allowAll}
							<div class="mt-1 border-t border-border pt-1">
								<Combobox.Item value={ALL_ACCOUNTS} label="All categories and accounts">All categories and accounts</Combobox.Item>
							</div>
						{/if}
					{/if}
				</Combobox.Content>
			</Combobox.Root>
		{/if}
	</div>
{/if}
