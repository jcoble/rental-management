<script lang="ts">
	import { Button } from '$lib/components/ui/button';
	import HelpPopover from '$lib/components/ui/HelpPopover.svelte';
	import { ACCOUNTING_HELP } from '$lib/accounting/accounting-help';
	import { getAccountingDetailMode } from './AccountingDetailMode.svelte';

	let { testid = 'accounting-detail-mode' }: { testid?: string } = $props();
	const detailMode = getAccountingDetailMode();

	function setMode(mode: 'simple' | 'advanced'): void {
		detailMode?.setMode(mode);
	}
</script>

<div class="inline-flex items-center gap-1 rounded-full border border-border bg-card p-1" data-testid={testid} data-mode={detailMode?.mode ?? 'simple'}>
	<span class="sr-only">Accounting detail level</span>
	<Button
		variant={detailMode?.mode === 'simple' ? 'secondary' : 'ghost'}
		size="sm"
		class="h-8 rounded-full px-3"
		aria-pressed={detailMode?.mode === 'simple'}
		aria-label="Use simple accounting detail"
		onclick={() => setMode('simple')}
		data-testid={`${testid}-simple`}
	>
		Simple
	</Button>
	<Button
		variant={detailMode?.mode === 'advanced' ? 'secondary' : 'ghost'}
		size="sm"
		class="h-8 rounded-full px-3"
		aria-pressed={detailMode?.mode === 'advanced'}
		aria-label="Use advanced accounting detail"
		onclick={() => setMode('advanced')}
		data-testid={`${testid}-advanced`}
	>
		Advanced
	</Button>
	<HelpPopover
		title={ACCOUNTING_HELP.detailMode.title}
		summary={ACCOUNTING_HELP.detailMode.summary}
		learnMoreUrl={ACCOUNTING_HELP.detailMode.href}
		testid="accounting-detail-mode-help"
	/>
</div>
