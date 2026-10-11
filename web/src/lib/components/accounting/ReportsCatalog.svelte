<script lang="ts">
	import { BarChart3, ClipboardList, FileBarChart, FileText, Receipt, ScrollText, TrendingUp, Users, Wallet } from '@lucide/svelte';

	const reports = [
		{ title: 'Profit & Loss', description: 'Income and expenses for a period.', href: '/accounting/profit-and-loss', icon: TrendingUp },
		{ title: 'Balance Sheet', description: 'Assets, liabilities, and equity as of a date.', href: '/accounting/balance-sheet', icon: FileBarChart },
		{ title: 'Trial Balance', description: 'Debit and credit balances by account.', href: '/accounting/trial-balance', icon: ClipboardList },
		{ title: 'General Ledger', description: 'Every posting in the accounting books.', href: '/accounting?tab=general-ledger', icon: ScrollText },
		{ title: 'Cash Flow', description: 'Cash in, cash out, and operating movement.', href: '/accounting?tab=cash-flow', icon: Wallet },
		{ title: 'Schedule E', description: 'Tax-ready rental income and expense categories.', href: '/tax', icon: Receipt },
		{ title: 'Rent ledger', description: 'Charges, payments, credits, and balances.', href: '/reports/rent-ledger', icon: BarChart3 },
		{ title: 'Owner statements', description: 'Owner earnings, expenses, and distributions.', href: '/owners-report', icon: Users },
		{ title: 'Year-end packet', description: 'The complete annual accounting handoff.', href: '/accounting/year-end', icon: FileText }
	];
</script>

<div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-3" data-testid="accounting-report-catalog">
	{#each reports as report (report.title)}
		<a href={report.href} class="group rounded-lg border bg-card p-4 transition-colors hover:border-primary/40 hover:bg-accent/40">
			<svelte:component this={report.icon} class="mb-3 h-5 w-5 text-primary" />
			<h3 class="font-semibold">{report.title}</h3>
			<p class="mt-1 text-sm text-muted-foreground">{report.description}</p>
		</a>
	{/each}
</div>
