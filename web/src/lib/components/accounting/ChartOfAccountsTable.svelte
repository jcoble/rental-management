<script lang="ts">
	import { ChevronDown, ChevronRight, LockKeyhole, Pencil } from '@lucide/svelte';

	import type { ChartOfAccountsRow } from '$lib/api/endpoints/accounting-books';
	import { Button } from '$lib/components/ui/button';
	import { getAccountingDetailMode } from '$lib/components/accounting/AccountingDetailMode.svelte';
	import {
		formatAccountTypeLabel
	} from '$lib/accounting/accounting-display';
	import { formatExpenseCategory } from '$lib/accounting/expense-categories';
	import {
		getChartOfAccountsSections,
		type CategorySectionId
	} from '$lib/accounting/chart-of-accounts-state';

	let {
		accounts = [],
		canEdit = false,
		collapsedSections = new Set<CategorySectionId>(),
		onedit,
		ondeactivate,
		onactivate,
		ontoggle
	}: {
		accounts?: ChartOfAccountsRow[];
		canEdit?: boolean;
		collapsedSections?: ReadonlySet<CategorySectionId>;
		onedit?: (account: ChartOfAccountsRow) => void;
		ondeactivate?: (account: ChartOfAccountsRow) => void;
		onactivate?: (account: ChartOfAccountsRow) => void;
		ontoggle?: (section: CategorySectionId) => void;
	} = $props();

	const detailMode = getAccountingDetailMode();
	const advanced = $derived(detailMode?.mode === 'advanced');
	const sections = $derived(getChartOfAccountsSections(accounts));
	const accountNames = $derived(new Map(accounts.map((account) => [account.id, account.name])));

	function parentLabel(parentAccountId: number | null): string {
		return parentAccountId === null ? '' : accountNames.get(parentAccountId) ?? 'Parent category';
	}
</script>

<div class="space-y-4" data-testid="chart-of-accounts-table">
	{#each sections as section (section.id)}
		<section class="overflow-hidden rounded-2xl border border-border bg-card" data-testid="chart-of-accounts-section-{section.id}">
			<button
				type="button"
				class="flex w-full items-center justify-between gap-4 border-b border-border px-4 py-3 text-left transition hover:bg-muted/40"
				aria-expanded={!collapsedSections.has(section.id)}
				aria-controls="chart-of-accounts-content-{section.id}"
				onclick={() => ontoggle?.(section.id)}
			>
				<span class="flex min-w-0 items-center gap-2">
					{#if collapsedSections.has(section.id)}
						<ChevronRight class="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
					{:else}
						<ChevronDown class="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
					{/if}
					<span class="font-semibold text-foreground">{section.label}</span>
					<span class="text-xs font-normal text-muted-foreground">{section.rows.length}</span>
				</span>
				{#if section.collapsedByDefault}
					<span class="text-xs text-muted-foreground">{collapsedSections.has(section.id) ? 'Show' : 'Hide'}</span>
				{/if}
			</button>

			{#if !collapsedSections.has(section.id)}
				<div id="chart-of-accounts-content-{section.id}">
					{#if section.rows.length === 0}
						<p class="px-4 py-6 text-sm text-muted-foreground">{section.emptyLabel}</p>
					{:else}
						<div class="overflow-x-auto">
							<table class="min-w-full divide-y divide-border text-sm">
								<thead class="bg-muted/30 text-left text-xs uppercase tracking-wide text-muted-foreground">
									<tr>
										<th class="px-4 py-3 font-medium">Category</th>
										<th class="px-4 py-3 font-medium">Details</th>
										{#if advanced}
											<th class="px-4 py-3 font-medium">Code</th>
											<th class="px-4 py-3 font-medium">Type</th>
											<th class="px-4 py-3 font-medium">Normal balance</th>
											<th class="px-4 py-3 font-medium">System key</th>
										{/if}
										<th class="px-4 py-3 text-right font-medium">Actions</th>
									</tr>
								</thead>
								<tbody class="divide-y divide-border">
									{#each section.rows as account (account.id)}
										<tr class="align-top">
											<td class="px-4 py-3">
												<div class="font-medium text-foreground">{account.name}</div>
												{#if account.parentAccountId !== null}
													<div class="mt-1 text-xs text-muted-foreground">Under {parentLabel(account.parentAccountId)}</div>
												{/if}
											</td>
											<td class="px-4 py-3 text-muted-foreground">
												<div class="flex items-center gap-1.5">
													{#if account.isSystem}
														<LockKeyhole class="size-3.5" aria-hidden="true" />
														<span>System account</span>
													{:else}
														<span>Custom category</span>
													{/if}
												</div>
												{#if account.scheduleECategory}
													<div class="mt-1 text-xs">Schedule E: {formatExpenseCategory(account.scheduleECategory)}</div>
												{/if}
												{#if !account.isActive}
													<div class="mt-1 text-xs text-warning">Inactive</div>
												{/if}
											</td>
											{#if advanced}
												<td class="whitespace-nowrap px-4 py-3 font-mono text-xs text-muted-foreground">{account.code}</td>
												<td class="whitespace-nowrap px-4 py-3 text-muted-foreground">{formatAccountTypeLabel(account.accountType)}</td>
												<td class="whitespace-nowrap px-4 py-3 text-muted-foreground">{account.normalBalance}</td>
												<td class="whitespace-nowrap px-4 py-3 font-mono text-xs text-muted-foreground">{account.systemKey ?? '—'}</td>
											{/if}
											<td class="px-4 py-3 text-right">
												{#if canEdit && !account.isSystem}
													<div class="flex justify-end gap-2">
														<Button variant="ghost" size="sm" class="gap-1.5" onclick={() => onedit?.(account)}>
															<Pencil class="size-3.5" aria-hidden="true" />
															<span>Rename</span>
														</Button>
														{#if account.isActive}
															<Button variant="ghost" size="sm" class="text-destructive hover:text-destructive" onclick={() => ondeactivate?.(account)}>
																Deactivate
															</Button>
														{:else}
															<Button variant="ghost" size="sm" onclick={() => onactivate?.(account)}>Activate</Button>
														{/if}
													</div>
												{:else}
													<span class="text-xs text-muted-foreground">Read-only</span>
												{/if}
											</td>
										</tr>
									{/each}
								</tbody>
							</table>
						</div>
					{/if}
				</div>
			{/if}
		</section>
	{/each}
</div>
