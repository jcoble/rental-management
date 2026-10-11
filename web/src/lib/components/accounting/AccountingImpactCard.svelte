<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { ArrowRight } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import HelpPopover from '$lib/components/ui/HelpPopover.svelte';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import {
		formatAccountingCurrency,
		formatAccountingDate,
		formatJournalPostingLabel,
		formatJournalLineSide,
		formatSimpleJournalLineLabel,
		formatSourceTypeLabel,
		getJournalLineEntry,
		type JournalLineDisplayInput
	} from '$lib/accounting/accounting-display';
	import { ACCOUNTING_HELP } from '$lib/accounting/accounting-help';
	import { normalizeTenantLedgerDescription } from '$lib/accounting/tenant-ledger-display';
	import {
		accountingBooks,
		type JournalSourceType
	} from '$lib/api/endpoints/accounting-books';
	import { getAccountingDetailMode } from './AccountingDetailMode.svelte';
	import JournalDetailDrawer from './JournalDetailDrawer.svelte';

	let {
		sourceType,
		sourceId,
		title = 'Accounting impact'
	}: {
		sourceType: JournalSourceType;
		sourceId: number;
		title?: string;
	} = $props();

	const authState = getAuthState();
	const detailMode = getAccountingDetailMode();
	const mode = $derived(detailMode?.mode ?? 'simple');
	const advanced = $derived(mode === 'advanced');
	let selectedJournalPublicId = $state<string | null>(null);

	const sourceJournalsQuery = createQuery(() => ({
		queryKey: ['accounting-source-journals', sourceType, sourceId],
		enabled: authState.isAuthenticated && sourceId > 0,
		queryFn: () => accountingBooks.sourceJournals({ sourceType, sourceId })
	}));

	function errorStatus(error: unknown): number | undefined {
		if (!error || typeof error !== 'object' || !('status' in error)) return undefined;
		const status = (error as { status?: unknown }).status;
		return typeof status === 'number' ? status : undefined;
	}

	const unauthorized = $derived(
		!authState.isAuthenticated || [401, 403, 404].includes(errorStatus(sourceJournalsQuery.error) ?? 0)
	);
	const journals = $derived(sourceJournalsQuery.data ?? []);

	function openJournal(publicId: string): void {
		selectedJournalPublicId = publicId;
	}

	function lineInput(
		line: (typeof journals)[number]['lines'][number],
		journal: (typeof journals)[number]
	): JournalLineDisplayInput {
		return {
			accountName: line.accountName,
			accountCode: line.accountCode,
			accountType: line.accountType,
			systemKey: line.systemKey,
			debitAmount: line.debitAmount,
			creditAmount: line.creditAmount,
			normalBalance: line.normalBalance,
			currency: journal.currency,
			effectiveOn: journal.effectiveOn,
			sourceType: journal.sourceType
		};
	}
</script>

{#if !unauthorized && sourceId > 0}
	{#if sourceJournalsQuery.isLoading}
		<section class="rounded-xl border border-border bg-card p-4" data-testid="accounting-impact-loading" aria-label="Loading accounting impact">
			<div class="h-5 w-40 animate-pulse rounded bg-muted"></div>
			<div class="mt-4 space-y-3">
				<div class="h-5 w-full animate-pulse rounded bg-muted"></div>
				<div class="h-5 w-5/6 animate-pulse rounded bg-muted"></div>
			</div>
		</section>
	{:else if !sourceJournalsQuery.isError && journals.length > 0}
		<section class="rounded-xl border border-border bg-card p-4" data-testid="accounting-impact-card">
			<div class="flex items-center gap-1.5">
				<h2 class="text-sm font-semibold">{title}</h2>
				<HelpPopover
					title={ACCOUNTING_HELP.accountingImpact.title}
					summary={ACCOUNTING_HELP.accountingImpact.summary}
					learnMoreUrl={ACCOUNTING_HELP.accountingImpact.href}
					testid="accounting-impact-help"
				/>
			</div>
			<div class="mt-3 divide-y divide-border">
				{#each journals as journal (journal.publicId)}
					<div class="py-3 first:pt-0 last:pb-0">
						<div class="flex items-start justify-between gap-4">
							<div class="min-w-0">
								<p class="truncate font-medium">
									{advanced ? formatSourceTypeLabel(journal.sourceType) : normalizeTenantLedgerDescription(journal.description)}
								</p>
								<p class="mt-1 text-xs text-muted-foreground">
									{formatAccountingDate(journal.effectiveOn)} · {formatJournalPostingLabel(journal.isReversal)}
								</p>
							</div>
							<p class="shrink-0 font-medium tabular-nums">{formatAccountingCurrency(journal.totalDebits)}</p>
						</div>
						<div class="mt-3 divide-y divide-border rounded-lg border border-border/70">
							{#each journal.lines as line (line.id)}
								{@const input = lineInput(line, journal)}
								{@const entry = getJournalLineEntry(input)}
								<div class="flex items-start justify-between gap-4 px-3 py-2 text-sm" data-testid={`accounting-impact-line-${line.id}`}>
									<span>{advanced ? `${line.accountCode} ${line.accountName} · ${formatJournalLineSide(entry.side)}` : formatSimpleJournalLineLabel(input)}</span>
									<span class="shrink-0 font-medium tabular-nums">{formatAccountingCurrency(entry.amount)}</span>
								</div>
							{/each}
						</div>
						<Button
							variant="link"
							size="sm"
							class="mt-2 h-auto px-0 py-0"
							onclick={() => openJournal(journal.publicId)}
							data-testid={`accounting-impact-open-${journal.publicId}`}
						>
							View accounting record <ArrowRight class="size-3.5" />
						</Button>
					</div>
				{/each}
			</div>
		</section>
	{:else if !sourceJournalsQuery.isError}
		<section class="rounded-xl border border-border bg-card p-4" data-testid="accounting-impact-empty">
			<h2 class="text-sm font-semibold">{title}</h2>
			<p class="mt-2 text-sm text-muted-foreground">No accounting entry — recorded before accounting was enabled.</p>
		</section>
	{/if}

	<JournalDetailDrawer bind:journalPublicId={selectedJournalPublicId} />
{/if}
