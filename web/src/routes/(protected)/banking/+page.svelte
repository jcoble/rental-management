<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { banking } from '$lib/api/endpoints/banking';
	import type {
		BankReviewQueueResponse,
		BankTransactionListResponse,
		BankingSummary,
		ExchangePlaidPublicTokenRequest,
		ImportBankTransactionsRequest,
		PlaidSettings
	} from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { formatDateOnly } from '$lib/utils/date';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import * as Card from '$lib/components/ui/card';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { Badge } from '$lib/components/ui/badge';
	import Pagination from '$lib/components/shared/Pagination.svelte';
	import { Ban, Check, Landmark, Link2, RefreshCw, RotateCcw, Upload, X } from '@lucide/svelte';

	type PlaidWindow = Window &
		typeof globalThis & {
			Plaid?: {
				create: (config: Record<string, unknown>) => { open: () => void; destroy?: () => void };
			};
		};

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	// shadcn Select binds a string; bits-ui treats '' as "no selection", so 'all' stands in for the
	// "no filter" choice and `statusFilter` maps it back to '' for the query.
	const ALL_STATUS = 'all';
	let statusValue = $state(ALL_STATUS);
	const statusFilter = $derived(statusValue === ALL_STATUS ? '' : statusValue);
	const STATUS_FILTER_OPTIONS = [
		{ value: ALL_STATUS, label: 'All' },
		{ value: 'Unmatched', label: 'Unmatched' },
		{ value: 'Matched', label: 'Matched' },
		// Ignored/personal lines are stored server-side as MatchStatus="Removed".
		{ value: 'Removed', label: 'Ignored' }
	];
	const TRANSACTION_PAGE_SIZE = 50;
	const REVIEW_QUEUE_PAGE_SIZE = 50;
	const statusFilterLabel = $derived(
		STATUS_FILTER_OPTIONS.find((o) => o.value === statusValue)?.label ?? 'All'
	);
	let transactionSkip = $state(0);
	let reviewSkip = $state(0);
	let exchangePublicToken = $state('');
	let exchangeInstitutionName = $state('Plaid Sandbox Bank');
	let exchangeAccountId = $state('');
	let exchangeAccountName = $state('Operating checking');
	let exchangeAccountMask = $state('');
	let importJson = $state(`{
  "provider": "Manual",
  "institutionName": "Sample Bank",
  "accountName": "Operating checking",
  "accountMask": "1234",
  "transactions": [
    {
      "providerTransactionId": "sample-deposit-001",
      "postedAt": "2026-06-03T00:00:00Z",
      "description": "Rent deposit",
      "amount": 1200,
      "isoCurrencyCode": "USD",
      "category": "Rent"
    }
  ]
}`);
	const isDev = import.meta.env.DEV;

	const summaryQuery = createQuery(() => ({
		queryKey: ['banking-summary', portfolioId],
		queryFn: () => banking.summary(),
		enabled: !!portfolioId
	}));

	const transactionsQuery = createQuery(() => ({
		queryKey: ['banking-transactions', portfolioId, statusFilter, transactionSkip],
		queryFn: () => banking.transactions({
			status: statusFilter || undefined,
			skip: transactionSkip,
			take: TRANSACTION_PAGE_SIZE
		}),
		enabled: !!portfolioId
	}));

	const plaidSettingsQuery = createQuery(() => ({
		queryKey: ['banking-plaid-settings', portfolioId],
		queryFn: () => banking.plaidSettings(),
		enabled: !!portfolioId
	}));

	const reviewQueueQuery = createQuery(() => ({
		queryKey: ['banking-review-queue', portfolioId, reviewSkip],
		queryFn: () => banking.reviewQueue({ skip: reviewSkip, take: REVIEW_QUEUE_PAGE_SIZE }),
		enabled: !!portfolioId
	}));

	const summary = $derived(summaryQuery.data as BankingSummary | undefined);
	const transactionPage = $derived(transactionsQuery.data as BankTransactionListResponse | undefined);
	const transactions = $derived(transactionPage?.items ?? []);
	const transactionTotalCount = $derived(transactionPage?.totalCount ?? transactions.length);
	const transactionPageEnd = $derived((transactionPage?.skip ?? transactionSkip) + transactions.length);
	const plaidSettings = $derived(plaidSettingsQuery.data as PlaidSettings | undefined);
	const reviewQueue = $derived(reviewQueueQuery.data as BankReviewQueueResponse | undefined);
	const reviewItems = $derived(reviewQueue?.items ?? []);
	const reviewCount = $derived(reviewQueue?.count ?? reviewItems.length);
	const reviewPageStart = $derived(reviewCount === 0 ? 0 : (reviewQueue?.skip ?? reviewSkip) + 1);
	const reviewPageEnd = $derived((reviewQueue?.skip ?? reviewSkip) + reviewItems.length);
	const canReviewPrevious = $derived((reviewQueue?.skip ?? reviewSkip) > 0);
	const canReviewNext = $derived(reviewPageEnd < reviewCount);

	$effect(() => {
		statusFilter;
		transactionSkip = 0;
	});

	function money(value: number) {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(value || 0);
	}

	// Local-time formatter for timestamps (e.g. lastSyncedAt). Date-only fields like postedAt
	// use formatDateOnly instead, so they don't shift a day back in behind-UTC zones.
	function date(value: string) {
		return new Date(value).toLocaleDateString();
	}

	function percent(value: number) {
		return `${Math.round((value || 0) * 100)}%`;
	}

	function refreshBanking() {
		queryClient.invalidateQueries({ queryKey: ['banking-summary', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['banking-transactions', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['banking-plaid-settings', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['banking-review-queue', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['accounting-summary', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['accounting-transactions', portfolioId] });
	}

	const linkTokenMutation = createMutation(() => ({
		mutationFn: () => banking.createPlaidLinkToken(),
		onSuccess: (result) => {
			if (!result.configured) {
				showError(result.message ?? 'Plaid settings are not configured.');
			}
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	const exchangeMutation = createMutation(() => ({
		mutationFn: (request: ExchangePlaidPublicTokenRequest) => banking.exchangePlaidPublicToken(request),
		onSuccess: (connection) => {
			showSuccess(`Connected ${connection.institutionName} ${connection.accountName}.`);
			exchangePublicToken = '';
			refreshBanking();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	const syncMutation = createMutation(() => ({
		mutationFn: (id: number) => banking.syncConnection(id),
		onSuccess: (result) => {
			showSuccess(`Synced ${result.importedCount} new transaction${result.importedCount === 1 ? '' : 's'}.`);
			refreshBanking();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	const importMutation = createMutation(() => ({
		mutationFn: (request: ImportBankTransactionsRequest) => banking.importTransactions(request),
		onSuccess: (result) => {
			showSuccess(`Imported ${result.importedCount} bank transaction${result.importedCount === 1 ? '' : 's'}.`);
			refreshBanking();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	const matchMutation = createMutation(() => ({
		mutationFn: ({ id, entityType, entityId }: { id: number; entityType: string; entityId: number }) =>
			banking.match(id, { entityType, entityId }),
		onSuccess: () => {
			showSuccess('Bank transaction matched.');
			refreshBanking();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	const clearMutation = createMutation(() => ({
		mutationFn: (id: number) => banking.clearMatch(id),
		onSuccess: () => {
			showSuccess('Match cleared.');
			refreshBanking();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	// Mark a bank line as personal / not business money. Server sets MatchStatus="Removed": it leaves
	// the unmatched queue and the business books, but stays visible under the "Ignored" filter and can
	// be brought back via Clear (un-ignore). Tracks a pending id for per-row button disabling.
	let pendingIgnoreId = $state<number | null>(null);
	const ignoreMutation = createMutation(() => ({
		mutationFn: (id: number) => banking.ignore(id),
		onMutate: (id: number) => {
			pendingIgnoreId = id;
		},
		onSuccess: () => {
			showSuccess('Marked personal — kept out of your books.');
			refreshBanking();
		},
		onError: (err) => showError(apiErrorMessage(err)),
		onSettled: () => {
			pendingIgnoreId = null;
		}
	}));

	let pendingReviewId = $state<number | null>(null);

	const confirmMatchMutation = createMutation(() => ({
		mutationFn: (id: number) => banking.confirmMatch(id),
		onMutate: (id: number) => {
			pendingReviewId = id;
		},
		onSuccess: () => {
			showSuccess('Confirmed. We won’t count this one twice.');
			reviewSkip = 0;
			refreshBanking();
		},
		onError: (err) => showError(apiErrorMessage(err)),
		onSettled: () => {
			pendingReviewId = null;
		}
	}));

	const dismissMatchMutation = createMutation(() => ({
		mutationFn: (id: number) => banking.dismissMatch(id),
		onMutate: (id: number) => {
			pendingReviewId = id;
		},
		onSuccess: () => {
			showSuccess('Got it — not a match.');
			reviewSkip = 0;
			refreshBanking();
		},
		onError: (err) => showError(apiErrorMessage(err)),
		onSettled: () => {
			pendingReviewId = null;
		}
	}));

	function importTransactions() {
		try {
			const parsed = JSON.parse(importJson) as ImportBankTransactionsRequest;
			if (!Array.isArray(parsed.transactions) || parsed.transactions.length === 0) {
				showError('Import JSON needs a transactions array.');
				return;
			}
			importMutation.mutate(parsed);
		} catch {
			showError('Import JSON is not valid.');
		}
	}

	async function loadPlaidScript() {
		const plaidWindow = window as PlaidWindow;
		if (plaidWindow.Plaid) return;
		await new Promise<void>((resolve, reject) => {
			const existing = document.querySelector<HTMLScriptElement>('script[src="https://cdn.plaid.com/link/v2/stable/link-initialize.js"]');
			if (existing) {
				existing.addEventListener('load', () => resolve(), { once: true });
				existing.addEventListener('error', () => reject(new Error('Plaid Link script failed to load.')), { once: true });
				return;
			}
			const script = document.createElement('script');
			script.src = 'https://cdn.plaid.com/link/v2/stable/link-initialize.js';
			script.async = true;
			script.onload = () => resolve();
			script.onerror = () => reject(new Error('Plaid Link script failed to load.'));
			document.head.appendChild(script);
		});
	}

	async function connectPlaid() {
		try {
			const result = await linkTokenMutation.mutateAsync();
			if (!result.configured || !result.linkToken) return;
			sessionStorage.setItem('plaid:linkToken', result.linkToken);
			await loadPlaidScript();
			const handler = (window as PlaidWindow).Plaid?.create({
				token: result.linkToken,
				onSuccess: async (public_token: string, metadata: Record<string, unknown>) => {
					const accounts = (metadata.accounts as Array<Record<string, string | undefined>> | undefined) ?? [];
					const account = accounts[0] ?? {};
					const institution = metadata.institution as Record<string, string | undefined> | undefined;
					await exchangeMutation.mutateAsync({
						publicToken: public_token,
						institutionName: institution?.name ?? 'Plaid bank',
						accountId: account.id ?? '',
						accountName: account.name ?? account.subtype ?? 'Linked account',
						accountMask: account.mask,
						accountType: account.type,
						accountSubtype: account.subtype
					});
					sessionStorage.removeItem('plaid:linkToken');
				},
				onExit: () => {
					sessionStorage.removeItem('plaid:linkToken');
				}
			});
			handler?.open();
		} catch (err) {
			showError(apiErrorMessage(err));
		}
	}

	function exchangeManualPublicToken() {
		if (!exchangePublicToken.trim() || !exchangeAccountId.trim()) {
			showError('Public token and account id are required.');
			return;
		}
		exchangeMutation.mutate({
			publicToken: exchangePublicToken,
			institutionName: exchangeInstitutionName,
			accountId: exchangeAccountId,
			accountName: exchangeAccountName || 'Linked account',
			accountMask: exchangeAccountMask || undefined,
			accountType: 'depository',
			accountSubtype: 'checking'
		});
	}
</script>

<svelte:head>
	<title>Banking - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="banking-page">
	<div class="mb-5 flex flex-wrap items-start justify-between gap-4">
		<div>
			<h1 class="flex items-center gap-2 text-2xl font-bold">
				<Landmark class="h-6 w-6 text-primary" />
				Banking
			</h1>
			<p class="mt-1 text-sm text-muted-foreground">
				Read-only bank reconciliation. Import or sync deposits and withdrawals, then match them to rent payments and expenses.
			</p>
		</div>
		<Button variant="outline" onclick={connectPlaid} disabled={linkTokenMutation.isPending || exchangeMutation.isPending || !plaidSettings?.configured}>
			<Link2 class="mr-1.5 h-4 w-4" />
			{linkTokenMutation.isPending || exchangeMutation.isPending ? 'Connecting...' : 'Connect Plaid'}
		</Button>
	</div>

	<div class="mb-6 grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
		<Card.Root class="gap-0 py-0">
			<Card.Content class="p-4">
				<p class="text-xs text-muted-foreground">Bank connections</p>
				<p class="font-mono text-2xl font-bold">{summary?.connectionCount ?? 0}</p>
			</Card.Content>
		</Card.Root>
		<Card.Root class="gap-0 py-0">
			<Card.Content class="p-4">
				<p class="text-xs text-muted-foreground">Transactions</p>
				<p class="font-mono text-2xl font-bold">{summary?.transactionCount ?? 0}</p>
			</Card.Content>
		</Card.Root>
		<Card.Root class="gap-0 py-0">
			<Card.Content class="p-4">
				<p class="text-xs text-muted-foreground">Unmatched</p>
				<p class="font-mono text-2xl font-bold text-[var(--warning)]">{summary?.unmatchedCount ?? 0}</p>
			</Card.Content>
		</Card.Root>
		<Card.Root class="gap-0 py-0">
			<Card.Content class="p-4">
				<p class="text-xs text-muted-foreground">Suggestions</p>
				<p class="font-mono text-2xl font-bold text-primary">{summary?.suggestedMatchCount ?? 0}</p>
			</Card.Content>
		</Card.Root>
	</div>

	<Card.Root class="mb-6 gap-0 py-0" data-testid="bank-review-queue">
		<Card.Header class="border-b border-border px-4 py-3">
			<div class="flex flex-wrap items-center justify-between gap-3">
				<div>
					<Card.Title class="flex items-center gap-2 text-base">
						<Check class="h-4 w-4 text-primary" />
						Review suggested matches
						{#if reviewCount > 0}
							<Badge variant="secondary" data-testid="bank-review-count">{reviewCount}</Badge>
						{/if}
					</Card.Title>
					<Card.Description>
						These bank deposits look like payments you already recorded — confirm so we don't count them twice.
					</Card.Description>
				</div>
			</div>
		</Card.Header>
		<Card.Content class="p-0">
			{#if reviewQueueQuery.isLoading}
				<p class="py-12 text-center text-sm text-muted-foreground">Loading suggested matches...</p>
			{:else if reviewQueueQuery.isError}
				<p class="py-12 text-center text-sm text-destructive">Could not load suggested matches.</p>
			{:else if reviewItems.length === 0}
				<p class="py-12 text-center text-sm text-muted-foreground" data-testid="bank-review-empty">
					Nothing to review.
				</p>
			{:else}
				<ul class="divide-y divide-border">
					{#each reviewItems as item (item.transaction.id)}
						<li
							class="flex flex-col gap-4 p-4 lg:flex-row lg:items-center lg:justify-between"
							data-testid="bank-review-item-{item.transaction.id}"
						>
							<div class="grid flex-1 gap-4 sm:grid-cols-2">
								<div class="rounded-md border border-border bg-muted/30 p-3">
									<p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Bank line</p>
									<p class="mt-1 font-medium">
										{item.transaction.merchantName || item.transaction.description}
									</p>
									<p class="text-xs text-muted-foreground">{formatDateOnly(item.transaction.postedAt)}</p>
									<p class="mt-1 font-mono text-sm {item.transaction.amount >= 0 ? 'text-[var(--success)]' : 'text-destructive'}">
										{money(item.transaction.amount)}
									</p>
								</div>
								<div class="rounded-md border border-border bg-muted/30 p-3">
									<div class="flex items-center justify-between gap-2">
										<p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">
											Already recorded as
										</p>
										<Badge variant="outline">{percent(item.suggestion.confidence)} match</Badge>
									</div>
									<p class="mt-1 font-medium">{item.suggestion.label}</p>
									<p class="text-xs text-muted-foreground">{item.suggestion.entityType}</p>
									<p class="mt-1 text-xs text-muted-foreground">{item.suggestion.reason}</p>
								</div>
							</div>
							<div class="flex shrink-0 gap-2 lg:flex-col">
								<Button
									size="sm"
									class="flex-1 lg:flex-none"
									onclick={() => confirmMatchMutation.mutate(item.transaction.id)}
									disabled={pendingReviewId === item.transaction.id}
									data-testid="bank-review-confirm-{item.transaction.id}"
								>
									<Check class="mr-1.5 h-4 w-4" />
									Confirm match
								</Button>
								<Button
									size="sm"
									variant="outline"
									class="flex-1 lg:flex-none"
									onclick={() => dismissMatchMutation.mutate(item.transaction.id)}
									disabled={pendingReviewId === item.transaction.id}
									data-testid="bank-review-dismiss-{item.transaction.id}"
								>
									<X class="mr-1.5 h-4 w-4" />
									Not a match
								</Button>
							</div>
						</li>
					{/each}
				</ul>
				{#if reviewCount > REVIEW_QUEUE_PAGE_SIZE}
					<div class="flex flex-wrap items-center justify-between gap-3 border-t border-border px-4 py-3">
						<p class="text-sm text-muted-foreground" data-testid="bank-review-page-status">
							Showing {reviewPageStart}-{reviewPageEnd} of {reviewCount}
						</p>
						<div class="flex gap-2">
							<Button
								size="sm"
								variant="outline"
								onclick={() => (reviewSkip = Math.max(0, reviewSkip - REVIEW_QUEUE_PAGE_SIZE))}
								disabled={!canReviewPrevious || reviewQueueQuery.isFetching}
							>
								Previous
							</Button>
							<Button
								size="sm"
								variant="outline"
								onclick={() => (reviewSkip += REVIEW_QUEUE_PAGE_SIZE)}
								disabled={!canReviewNext || reviewQueueQuery.isFetching}
							>
								Next
							</Button>
						</div>
					</div>
				{/if}
			{/if}
		</Card.Content>
	</Card.Root>

	<div class="grid gap-6 xl:grid-cols-[1fr_420px]">
		<Card.Root class="gap-0 py-0">
			<Card.Header class="border-b border-border px-4 py-3">
				<div class="flex flex-wrap items-center justify-between gap-3">
					<div>
						<Card.Title class="text-base">Bank transactions</Card.Title>
						<Card.Description>Deposits are positive. Withdrawals are negative. Mark personal lines (coffee, rideshare) <span class="font-medium">Ignore</span> to keep them out of your books.</Card.Description>
					</div>
					<Select.Root type="single" bind:value={statusValue}>
						<Select.Trigger class="h-9 w-36" data-testid="banking-status-filter">
							{statusFilterLabel}
						</Select.Trigger>
						<Select.Content>
							{#each STATUS_FILTER_OPTIONS as opt (opt.value)}
								<Select.Item value={opt.value} label={opt.label}>{opt.label}</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
				</div>
			</Card.Header>
			<Card.Content class="p-0">
				{#if transactionsQuery.isLoading}
					<p class="py-12 text-center text-sm text-muted-foreground">Loading bank transactions...</p>
				{:else if transactionsQuery.isError}
					<p class="py-12 text-center text-sm text-destructive">Could not load bank transactions.</p>
				{:else if transactions.length === 0}
					<p class="py-12 text-center text-sm text-muted-foreground">
						{statusFilter === 'Removed' ? 'No ignored bank lines.' : 'No bank transactions yet.'}
					</p>
				{:else}
					<div class="overflow-x-auto">
						<table class="w-full text-sm">
							<thead>
								<tr class="border-b border-border bg-muted/50">
									<th class="px-4 py-3 text-left font-medium text-muted-foreground">Date</th>
									<th class="px-4 py-3 text-left font-medium text-muted-foreground">Description</th>
									<th class="px-4 py-3 text-right font-medium text-muted-foreground">Amount</th>
									<th class="px-4 py-3 text-left font-medium text-muted-foreground">Match</th>
								</tr>
							</thead>
							<tbody>
								{#each transactions as transaction (transaction.id)}
									<tr class="border-b border-border last:border-0" data-testid="bank-transaction-{transaction.id}">
										<td class="whitespace-nowrap px-4 py-3">{formatDateOnly(transaction.postedAt)}</td>
										<td class="px-4 py-3">
											<p class="font-medium">{transaction.merchantName || transaction.description}</p>
											<p class="text-xs text-muted-foreground">{transaction.institutionName} / {transaction.accountName}</p>
										</td>
										<td class="whitespace-nowrap px-4 py-3 text-right font-mono {transaction.amount >= 0 ? 'text-[var(--success)]' : 'text-destructive'}">
											{money(transaction.amount)}
										</td>
										<td class="min-w-64 px-4 py-3">
											{#if transaction.matchStatus === 'Matched'}
												<div class="flex flex-wrap items-center gap-2">
													<span class="m3-tone-chip border m3-tone--success rounded-full px-2 py-1 text-xs font-medium">Matched</span>
													<Button
														size="sm"
														variant="outline"
														onclick={() => clearMutation.mutate(transaction.id)}
														disabled={clearMutation.isPending}
													>
														Clear
													</Button>
												</div>
											{:else if transaction.matchStatus === 'Removed'}
												<div class="flex flex-wrap items-center gap-2">
													<span class="rounded-full bg-muted px-2 py-1 text-xs font-medium text-muted-foreground">Personal · Ignored</span>
													<Button
														size="sm"
														variant="outline"
														onclick={() => clearMutation.mutate(transaction.id)}
														disabled={clearMutation.isPending}
														data-testid="bank-transaction-unignore-{transaction.id}"
													>
														<RotateCcw class="mr-1.5 h-3.5 w-3.5" />
														Un-ignore
													</Button>
												</div>
											{:else if transaction.suggestedMatch}
												<div class="space-y-2">
													<p class="text-xs text-muted-foreground">{transaction.suggestedMatch.reason}</p>
													<div class="flex flex-wrap items-center gap-2">
														<Button
															size="sm"
															onclick={() => matchMutation.mutate({
																id: transaction.id,
																entityType: transaction.suggestedMatch!.entityType,
																entityId: transaction.suggestedMatch!.entityId
															})}
															disabled={matchMutation.isPending}
														>
															Match {transaction.suggestedMatch.label}
														</Button>
														<Button
															size="sm"
															variant="ghost"
															class="text-muted-foreground"
															onclick={() => ignoreMutation.mutate(transaction.id)}
															disabled={pendingIgnoreId === transaction.id}
															data-testid="bank-transaction-ignore-{transaction.id}"
														>
															<Ban class="mr-1.5 h-3.5 w-3.5" />
															{pendingIgnoreId === transaction.id ? 'Ignoring…' : 'Ignore'}
														</Button>
													</div>
												</div>
											{:else}
												<div class="flex flex-wrap items-center gap-2">
													<span class="text-xs text-muted-foreground">No suggestion yet</span>
													<Button
														size="sm"
														variant="ghost"
														class="text-muted-foreground"
														onclick={() => ignoreMutation.mutate(transaction.id)}
														disabled={pendingIgnoreId === transaction.id}
														data-testid="bank-transaction-ignore-{transaction.id}"
													>
														<Ban class="mr-1.5 h-3.5 w-3.5" />
														{pendingIgnoreId === transaction.id ? 'Ignoring…' : 'Ignore'}
													</Button>
												</div>
											{/if}
										</td>
									</tr>
								{/each}
							</tbody>
						</table>
					</div>
					<div class="border-t border-border px-4 py-3">
						<Pagination
							bind:skip={transactionSkip}
							take={TRANSACTION_PAGE_SIZE}
							count={transactions.length}
							hasNext={transactionPageEnd < transactionTotalCount}
							testid="banking-transactions-pagination"
						/>
					</div>
				{/if}
			</Card.Content>
		</Card.Root>

		<div class="space-y-4">
			<Card.Root class="gap-0 py-0">
					<Card.Header class="border-b border-border px-4 py-3">
						<Card.Title class="flex items-center gap-2 text-base">
							<Link2 class="h-4 w-4" />
							Plaid
						</Card.Title>
						<Card.Description>Read-only bank connection through Plaid Link.</Card.Description>
					</Card.Header>
					<Card.Content class="space-y-3 p-4">
						<div class="rounded-md border border-border p-3">
							<p class="text-sm font-medium">
								{plaidSettings?.configured ? 'Plaid is configured' : 'Plaid is not configured'}
							</p>
							<p class="mt-1 text-xs text-muted-foreground">
								Environment: {plaidSettings?.plaidEnvironment ?? 'sandbox'}
							</p>
							{#if !plaidSettings?.configured}
								<p class="mt-2 text-xs text-muted-foreground">
									Set <code>Plaid:ClientId</code> and <code>Plaid:Secret</code> in server secrets to enable bank connections.
								</p>
							{/if}
						</div>
						<div class="flex flex-wrap gap-2">
							<Button variant="outline" onclick={connectPlaid} disabled={!plaidSettings?.configured || linkTokenMutation.isPending || exchangeMutation.isPending}>
								<Link2 class="mr-1.5 h-4 w-4" />
							Connect account
						</Button>
					</div>
				</Card.Content>
			</Card.Root>

			<Card.Root class="gap-0 py-0">
				<Card.Header class="border-b border-border px-4 py-3">
					<Card.Title class="text-base">Connections</Card.Title>
					<Card.Description>Sync pulls new bank lines from Plaid and keeps existing matches.</Card.Description>
				</Card.Header>
				<Card.Content class="space-y-3 p-4">
					{#if (summary?.connections.length ?? 0) === 0}
						<p class="text-sm text-muted-foreground">No bank connections yet.</p>
					{:else}
						{#each summary?.connections ?? [] as connection (connection.id)}
							<div class="rounded-md border border-border p-3">
								<div class="flex items-start justify-between gap-3">
									<div>
										<p class="font-medium">{connection.institutionName}</p>
										<p class="text-xs text-muted-foreground">{connection.accountName}{connection.accountMask ? ` • ${connection.accountMask}` : ''}</p>
										<p class="mt-1 text-xs text-muted-foreground">Last synced {connection.lastSyncedAt ? date(connection.lastSyncedAt) : 'never'}</p>
									</div>
									<Button size="sm" variant="outline" onclick={() => syncMutation.mutate(connection.id)} disabled={syncMutation.isPending || connection.provider !== 'Plaid'}>
										<RefreshCw class="mr-1.5 h-4 w-4" />
										Sync
									</Button>
								</div>
							</div>
						{/each}
					{/if}
				</Card.Content>
			</Card.Root>

			{#if isDev}
				<Card.Root class="gap-0 py-0">
					<Card.Header class="border-b border-border px-4 py-3">
						<Card.Title class="text-base">Sandbox token exchange</Card.Title>
						<Card.Description>Development-only Plaid sandbox/debug path when Link is not available.</Card.Description>
					</Card.Header>
					<Card.Content class="space-y-3 p-4">
						<input class="h-9 w-full rounded-md border border-input bg-background px-3 text-sm" placeholder="public-sandbox-token" bind:value={exchangePublicToken} />
						<input class="h-9 w-full rounded-md border border-input bg-background px-3 text-sm" placeholder="Plaid account id" bind:value={exchangeAccountId} />
						<input class="h-9 w-full rounded-md border border-input bg-background px-3 text-sm" placeholder="Institution name" bind:value={exchangeInstitutionName} />
						<input class="h-9 w-full rounded-md border border-input bg-background px-3 text-sm" placeholder="Account name" bind:value={exchangeAccountName} />
						<input class="h-9 w-full rounded-md border border-input bg-background px-3 text-sm" placeholder="Mask" bind:value={exchangeAccountMask} />
						<Button class="w-full" variant="outline" onclick={exchangeManualPublicToken} disabled={exchangeMutation.isPending}>
							Exchange public token
						</Button>
					</Card.Content>
				</Card.Root>
			{/if}

			<Card.Root class="gap-0 py-0">
				<Card.Header class="border-b border-border px-4 py-3">
					<Card.Title class="flex items-center gap-2 text-base">
						<Upload class="h-4 w-4" />
						Import bank lines
					</Card.Title>
					<Card.Description>Manual import stays available for CSV/JSON exports and scanner cleanup.</Card.Description>
				</Card.Header>
				<Card.Content class="space-y-3 p-4">
					<textarea
						class="min-h-80 w-full rounded-md border border-input bg-background p-3 font-mono text-xs"
						bind:value={importJson}
						data-testid="banking-import-json"
					></textarea>
					<Button class="w-full" onclick={importTransactions} disabled={importMutation.isPending}>
						{importMutation.isPending ? 'Importing...' : 'Import transactions'}
					</Button>
				</Card.Content>
			</Card.Root>
		</div>
	</div>
</div>
