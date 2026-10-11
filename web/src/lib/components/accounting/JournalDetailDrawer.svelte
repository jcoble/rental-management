<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { X } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import HelpPopover from '$lib/components/ui/HelpPopover.svelte';
	import * as Drawer from '$lib/components/ui/drawer';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import {
		formatAccountingCurrency,
		formatAccountingDate,
		formatAccountingDateTime,
		formatBalancedStatus,
		formatJournalLineSide,
		formatSimpleJournalLineLabel,
		formatSourceRecordLabel,
		getJournalLineEntry,
		type JournalLineDisplayInput
	} from '$lib/accounting/accounting-display';
	import { ACCOUNTING_HELP } from '$lib/accounting/accounting-help';
	import { normalizeTenantLedgerDescription } from '$lib/accounting/tenant-ledger-display';
	import { accountingBooks, type JournalDetailLine } from '$lib/api/endpoints/accounting-books';
	import { getAccountingDetailMode } from './AccountingDetailMode.svelte';
	import { formatStatusLabel } from '$lib/utils/status-labels';

	let {
		journalPublicId = $bindable<string | null>(null)
	}: {
		journalPublicId?: string | null;
	} = $props();

	const authState = getAuthState();
	const detailMode = getAccountingDetailMode();
	const mode = $derived(detailMode?.mode ?? 'simple');
	const advanced = $derived(mode === 'advanced');
	let open = $state(false);

	const journalQuery = createQuery(() => ({
		queryKey: ['accounting-journal-detail', journalPublicId],
		enabled: authState.isAuthenticated && !!journalPublicId,
		queryFn: () => accountingBooks.journalDetail(journalPublicId as string)
	}));
	function errorStatus(error: unknown): number | undefined {
		if (!error || typeof error !== 'object' || !('status' in error)) return undefined;
		const status = (error as { status?: unknown }).status;
		return typeof status === 'number' ? status : undefined;
	}

	const unauthorized = $derived(
		!authState.isAuthenticated ||
			[401, 403, 404].includes(errorStatus(journalQuery.error) ?? 0)
	);
	const isLoading = $derived(journalQuery.isLoading);

	$effect(() => {
		open = !!journalPublicId;
	});

	function close(): void {
		open = false;
		journalPublicId = null;
	}

	function lineInput(line: JournalDetailLine): JournalLineDisplayInput {
		return {
			accountName: line.accountName,
			accountCode: line.accountCode,
			accountType: line.accountType,
			systemKey: line.systemKey,
			debitAmount: line.debitAmount,
			creditAmount: line.creditAmount,
			normalBalance: line.normalBalance,
			currency: journalQuery.data?.currency ?? 'USD',
			effectiveOn: journalQuery.data?.effectiveOn,
			sourceType: journalQuery.data?.sourceType
		};
	}

	function sourceRecordHref(sourceType: string, sourceId: number): string {
		const query = new URLSearchParams({ sourceType, sourceId: String(sourceId) });
		return `/accounting?${query.toString()}`;
	}

	function documentHref(documentId: number): string {
		return `/document-file/${documentId}`;
	}

	function bankEvidenceLabel(detail: NonNullable<typeof journalQuery.data>): string {
		const evidence = detail.bankReconciliationEvidence;
		if (!evidence) return '—';
		const account = evidence.bankAccountLabel ?? 'Bank account';
		const status = evidence.status ? ` · ${formatStatusLabel(evidence.status)}` : '';
		const matched = evidence.matchedOn ? ` · matched ${formatAccountingDate(evidence.matchedOn)}` : '';
		return `${account}${status}${matched}`;
	}
</script>

{#if journalPublicId && !unauthorized}
	<Drawer.Root
		bind:open
		direction="right"
		onOpenChange={(next) => {
			if (!next) close();
		}}
	>
		<Drawer.Content class="w-full overflow-y-auto sm:max-w-xl" data-testid="journal-detail-drawer">
			{#if isLoading}
				<div class="space-y-4 p-6" role="status" aria-label="Loading accounting record">
					<div class="h-6 w-56 animate-pulse rounded bg-muted"></div>
					<div class="h-4 w-72 animate-pulse rounded bg-muted"></div>
					<div class="space-y-3 pt-4">
						<div class="h-12 animate-pulse rounded-md bg-muted"></div>
						<div class="h-12 animate-pulse rounded-md bg-muted"></div>
						<div class="h-12 animate-pulse rounded-md bg-muted"></div>
					</div>
				</div>
			{:else if journalQuery.isError}
				<div class="p-6" data-testid="journal-detail-error">
					<Drawer.Header class="p-0">
						<Drawer.Title>Accounting record</Drawer.Title>
						<Drawer.Description>We could not load this accounting record.</Drawer.Description>
					</Drawer.Header>
					<Button class="mt-5" variant="outline" onclick={() => journalQuery.refetch()}>Try again</Button>
				</div>
			{:else if journalQuery.data}
				{@const detail = journalQuery.data}
				<div class="flex items-start justify-between gap-4 border-b border-border px-6 py-5">
					<div>
						<p class="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
							Accounting record · {formatAccountingDate(detail.effectiveOn)}
						</p>
						<h2 class="mt-1 text-lg font-semibold">{normalizeTenantLedgerDescription(detail.description)}</h2>
						<p class="mt-1 text-sm text-muted-foreground">{formatSourceRecordLabel(detail.sourceType, detail.sourceId)}</p>
					</div>
					<Button variant="ghost" size="icon" aria-label="Close accounting record" onclick={close}>
						<X class="size-5" />
					</Button>
				</div>

				<div class="space-y-6 px-6 py-5">
					<section aria-labelledby="journal-detail-lines-heading">
						<div class="flex items-center gap-1.5">
							<h3 id="journal-detail-lines-heading" class="text-sm font-semibold uppercase tracking-wide text-muted-foreground">
								{advanced ? 'Detail' : 'What this did'}
							</h3>
							<HelpPopover
								title={ACCOUNTING_HELP.journalDetail.title}
								summary={ACCOUNTING_HELP.journalDetail.summary}
								learnMoreUrl={ACCOUNTING_HELP.journalDetail.href}
								testid="journal-detail-help"
							/>
						</div>
						<div class="mt-3 divide-y divide-border rounded-lg border border-border">
							{#each detail.lines as line (line.id)}
								{@const input = lineInput(line)}
								{@const entry = getJournalLineEntry(input)}
								<div class="flex items-start justify-between gap-4 px-3 py-3 text-sm" data-testid={`journal-detail-line-${line.id}`}>
									{#if advanced}
										<div class="min-w-0">
											<p class="font-medium">{line.accountCode} {line.accountName}</p>
											{#if line.memo}<p class="mt-0.5 text-xs text-muted-foreground">{line.memo}</p>{/if}
										</div>
										<div class="shrink-0 text-right">
											<p class="font-medium">{formatJournalLineSide(entry.side)} {formatAccountingCurrency(entry.amount, detail.currency)}</p>
										</div>
									{:else}
										<p class="min-w-0 font-medium">{formatSimpleJournalLineLabel(input)}</p>
										<p class="shrink-0 font-medium">{formatAccountingCurrency(entry.amount, detail.currency)}</p>
									{/if}
								</div>
							{/each}
							<div class="flex items-center justify-between gap-4 bg-muted/30 px-3 py-3 text-sm font-medium">
								<span>Balanced</span>
								<span>{formatBalancedStatus(detail.isBalanced)}</span>
							</div>
						</div>
					</section>

					<section class="space-y-3" aria-label="Accounting record references">
						<div class="grid gap-3 text-sm sm:grid-cols-[9rem_1fr]">
							<span class="text-muted-foreground">Source record</span>
							<a class="font-medium text-primary underline-offset-4 hover:underline" href={sourceRecordHref(detail.sourceType, detail.sourceId)}>
								{formatSourceRecordLabel(detail.sourceType, detail.sourceId)} <span aria-hidden="true">Open</span>
							</a>
							<span class="text-muted-foreground">Document</span>
							<span>
								{#if detail.documentIds.length === 0}
									—
								{:else}
									{#each detail.documentIds as documentId, index}{index ? ', ' : ''}<a class="font-medium text-primary underline-offset-4 hover:underline" href={documentHref(documentId)} target="_blank" rel="noreferrer">Document #{documentId}</a>{/each}
								{/if}
							</span>
							<span class="text-muted-foreground">Entered by</span>
							<span>{detail.actor ?? '—'} · {formatAccountingDateTime(detail.postedAtUtc)}</span>
							<span class="text-muted-foreground">Effective</span>
							<span>{formatAccountingDate(detail.effectiveOn)} · Posted {formatAccountingDate(detail.postedAtUtc)}</span>
							<span class="text-muted-foreground">Bank match</span>
							<span>{bankEvidenceLabel(detail)}</span>
						</div>
					</section>

					<section class="space-y-3 border-t border-border pt-4" aria-label="Reversal and audit history">
						<div class="grid gap-3 text-sm sm:grid-cols-[9rem_1fr]">
							<span class="text-muted-foreground">Reversal</span>
							<span>
								{#if detail.reversesJournalEntryPublicId}
									Reverses
									<Button variant="link" size="sm" class="h-auto px-1 py-0" onclick={() => (journalPublicId = detail.reversesJournalEntryPublicId)}>
										{detail.reversesJournalEntryPublicId}
									</Button>
								{:else if detail.reversalPublicIds.length > 0}
									Reversed by
									{#each detail.reversalPublicIds as reversalPublicId, index}
										{index ? ', ' : ''}<Button variant="link" size="sm" class="h-auto px-1 py-0" onclick={() => (journalPublicId = reversalPublicId)}>{reversalPublicId}</Button>
									{/each}
								{:else}
									—
								{/if}
							</span>
							<span class="text-muted-foreground">Audit</span>
							<span>
								{#if detail.auditLink}
									<a class="font-medium text-primary underline-offset-4 hover:underline" href={detail.auditLink}>View audit history</a>
								{:else}
									—
								{/if}
							</span>
						</div>
					</section>
				</div>
			{/if}
		</Drawer.Content>
	</Drawer.Root>
{/if}
