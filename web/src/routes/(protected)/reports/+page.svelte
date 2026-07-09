<!--
  Reports Hub — catalog. Grouped cards (one per report) from GET /reports/catalog.
  Clicking a non-external report opens the generic viewer at /reports/[key]; external reports
  (Schedule E / Owner Statement / Year-End Packet) deep-link to their existing pages.
  A "Need a report you don't see?" card opens the user's email client (mailto) with the request
  pre-filled — there is no server-side intake yet, so it never claims a request was recorded.
-->
<script lang="ts">
	import { goto } from '$app/navigation';
	import { createQuery } from '@tanstack/svelte-query';
	import { reports, type ReportCatalogEntry } from '$lib/api/endpoints/reports';
	import { showInfo, showError, apiErrorMessage } from '$lib/utils/toast';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import {
		BarChart3,
		Receipt,
		FileBarChart,
		Wallet,
		Wrench,
		Users,
		Building2,
		ScrollText,
		CalendarClock,
		PiggyBank,
		ClipboardList,
		TrendingUp,
		FileText,
		AlertTriangle,
		ExternalLink,
		ChevronRight,
		Lightbulb
	} from '@lucide/svelte';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';

	const catalogQuery = createQuery(() => ({
		queryKey: ['reports-catalog'],
		queryFn: () => reports.catalog(),
		staleTime: 5 * 60_000,
	}));

	type IconType = typeof BarChart3;

	// Icon per report key (fallback per category) — purely cosmetic; the catalog is data-driven.
	const reportIcons: Record<string, IconType> = {
		'income-expense-statement': TrendingUp,
		'property-pnl-summary': Building2,
		'general-ledger': ScrollText,
		'cash-flow': Wallet,
		'schedule-e': Receipt,
		'year-end-packet': FileText,
		'rent-roll': ClipboardList,
		'rent-ledger': ScrollText,
		delinquency: AlertTriangle,
		'owner-statement': FileBarChart,
		'owner-distributions': Users,
		occupancy: Building2,
		'lease-expirations': CalendarClock,
		'security-deposit-register': PiggyBank,
		'vendor-1099': Receipt,
		'work-orders': Wrench,
	};
	const categoryIcons: Record<string, IconType> = {
		accounting: Wallet,
		'rent-payments': ClipboardList,
		owners: Users,
		operations: Wrench,
	};

	function iconFor(entry: ReportCatalogEntry, categoryKey: string): IconType {
		return reportIcons[entry.key] ?? categoryIcons[categoryKey] ?? BarChart3;
	}

	// External reports deep-link to their existing pages (the hub references, never reimplements).
	const externalLinks: Record<string, string> = {
		'schedule-e': '/tax',
		'year-end-packet': '/tax',
		'owner-statement': '/owners-report',
	};

	function openReport(entry: ReportCatalogEntry, categoryKey: string) {
		if (entry.external) {
			const href = externalLinks[entry.key];
			if (href) {
				goto(href);
			} else {
				showError('This report is not available yet.');
			}
			return;
		}
		goto(`/reports/${entry.key}`);
	}

	// --- Custom report request --------------------------------------------------------------------
	// There is no server-side intake endpoint for custom-report requests (ReportsController is
	// read-only and no operator/support inbox is configured). Rather than fake a "we've received it"
	// confirmation for a request that never leaves the browser, we open the user's email client with
	// the request pre-filled so it actually reaches the team. Address is configurable via
	// VITE_SUPPORT_EMAIL (same pattern as VITE_API_URL in $lib/config).
	const SUPPORT_EMAIL =
		(import.meta.env.VITE_SUPPORT_EMAIL as string | undefined) || 'jcoble@rentalcommand.net';
	let customRequest = $state('');

	function submitCustomRequest() {
		const text = customRequest.trim();
		if (!text) {
			showError('Tell us a little about the report you need.');
			return;
		}
		const subject = 'Custom report request';
		const body = `I'd like a report that isn't in Reports yet:\n\n${text}\n`;
		const href = `mailto:${SUPPORT_EMAIL}?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`;
		// Hand off to the user's mail client; nothing is "recorded" server-side, so we don't claim it.
		window.location.href = href;
		showInfo("Opening your email app — send the message and we'll take it from there.");
	}
</script>

<svelte:head>
	<title>Reports - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="reports-page">
	<PageHeader
		class="mb-5"
		band
		art={7}
		tone="amber"
		eyebrow="Money"
		title="Reports"
		description="Pick a report, set the dates and properties, then export or print."
		data-testid="reports-header"
	/>

	{#if catalogQuery.isLoading}
		<div class="space-y-6">
			{#each Array(3) as _, gi (gi)}
				<div>
					<div class="mb-3 h-5 w-40 animate-pulse rounded bg-muted"></div>
					<div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
						{#each Array(3) as _, ci (ci)}
							<div class="h-28 animate-pulse rounded-lg border bg-muted/40"></div>
						{/each}
					</div>
				</div>
			{/each}
		</div>
	{:else if catalogQuery.isError}
		<Card.Root class="border-destructive/30 bg-destructive/5">
			<Card.Content class="flex items-center justify-between gap-4 p-4">
				<div class="flex items-center gap-3">
					<AlertTriangle class="h-5 w-5 shrink-0 text-destructive" />
					<p class="text-sm text-destructive">{apiErrorMessage(catalogQuery.error, 'Could not load the reports catalog.')}</p>
				</div>
				<Button variant="outline" size="sm" onclick={() => catalogQuery.refetch()}>Retry</Button>
			</Card.Content>
		</Card.Root>
	{:else}
		<div class="space-y-8" data-testid="reports-catalog">
			{#each catalogQuery.data?.categories ?? [] as category (category.key)}
				{@const CategoryIcon = categoryIcons[category.key] ?? BarChart3}
				<section>
					<div class="mb-3 flex items-center gap-2">
						<CategoryIcon class="h-4 w-4 text-muted-foreground" />
						<h2 class="text-sm font-semibold uppercase tracking-wide text-muted-foreground">{category.title}</h2>
					</div>
					<div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
						{#each category.reports as entry (entry.key)}
							{@const Icon = iconFor(entry, category.key)}
							<button
								type="button"
								onclick={() => openReport(entry, category.key)}
								class="group flex h-full flex-col rounded-lg border bg-card p-4 text-left transition-colors hover:border-primary/40 hover:bg-accent/40 focus:outline-none focus-visible:ring-2 focus-visible:ring-ring"
								data-testid="report-card-{entry.key}"
							>
								<div class="mb-2 flex items-center justify-between">
									<span class="flex h-9 w-9 items-center justify-center rounded-md bg-primary/10 text-primary ring-1 ring-inset ring-primary/20">
										<Icon class="h-4 w-4" />
									</span>
									{#if entry.external}
										<span class="inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[10px] font-medium text-muted-foreground">
											<ExternalLink class="h-3 w-3" /> Opens existing
										</span>
									{:else}
										<ChevronRight class="h-4 w-4 text-muted-foreground/50 transition-transform group-hover:translate-x-0.5 group-hover:text-primary" />
									{/if}
								</div>
								<h3 class="font-semibold leading-tight">{entry.title}</h3>
								<p class="mt-1 text-sm text-muted-foreground">{entry.description}</p>
							</button>
						{/each}
					</div>
				</section>
			{/each}

			<!-- Custom report request CTA -->
			<section>
				<Card.Root class="border-dashed bg-muted/30" data-testid="custom-report-request">
					<Card.Content class="p-5">
						<div class="flex items-start gap-3">
							<span class="flex h-9 w-9 shrink-0 items-center justify-center rounded-md bg-[color-mix(in_srgb,var(--warning)_10%,transparent)] text-[var(--warning)] ring-1 ring-inset ring-[color-mix(in_srgb,var(--warning)_20%,transparent)]">
								<Lightbulb class="h-4 w-4" />
							</span>
							<div class="flex-1">
								<h3 class="font-semibold">Need a report you don't see?</h3>
								<p class="mt-1 text-sm text-muted-foreground">
									Describe what you're trying to figure out and we'll email it to our team to look at
									adding. Custom reports are a paid add-on, so we'll quote it before building anything.
								</p>
								<div class="mt-3 flex flex-col gap-2 sm:flex-row sm:items-end">
									<textarea
										bind:value={customRequest}
										rows="2"
										placeholder="e.g. A report of every late fee charged this year by property…"
										class="flex-1 rounded-md border border-input bg-background px-3 py-2 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
										data-testid="custom-report-input"
									></textarea>
									<Button onclick={submitCustomRequest} data-testid="custom-report-submit">Email request</Button>
								</div>
							</div>
						</div>
					</Card.Content>
				</Card.Root>
			</section>
		</div>
	{/if}
</div>
