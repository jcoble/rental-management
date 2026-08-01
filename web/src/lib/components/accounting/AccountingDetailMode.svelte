<script module lang="ts">
	import { getContext } from 'svelte';

	export type AccountingDetailMode = 'simple' | 'advanced';

	export interface AccountingDetailModeContext {
		readonly mode: AccountingDetailMode;
		setMode(mode: AccountingDetailMode): void;
		toggle(): void;
	}

	export const ACCOUNTING_DETAIL_MODE_CONTEXT = Symbol('rc.accounting.detail-mode.v1');

	export function getAccountingDetailMode(): AccountingDetailModeContext | null {
		return getContext<AccountingDetailModeContext | null>(ACCOUNTING_DETAIL_MODE_CONTEXT) ?? null;
	}
</script>

<script lang="ts">
	import type { Snippet } from 'svelte';
	import { setContext } from 'svelte';
	import { Button } from '$lib/components/ui/button';

	const STORAGE_KEY = 'rc.accounting.detail-mode.v1';

	let {
		children,
		class: className,
		testid = 'accounting-detail-mode'
	}: {
		children?: Snippet;
		class?: string;
		testid?: string;
	} = $props();

	let mode = $state<AccountingDetailMode>('simple');

	function isAccountingDetailMode(value: string | null): value is AccountingDetailMode {
		return value === 'simple' || value === 'advanced';
	}

	function setMode(next: AccountingDetailMode): void {
		mode = next;
		if (typeof window !== 'undefined') {
			window.localStorage.setItem(STORAGE_KEY, next);
		}
	}

	const context: AccountingDetailModeContext = {
		get mode() {
			return mode;
		},
		setMode,
		toggle() {
			setMode(mode === 'simple' ? 'advanced' : 'simple');
		}
	};

	setContext(ACCOUNTING_DETAIL_MODE_CONTEXT, context);

	$effect(() => {
		if (typeof window === 'undefined') return;
		const stored = window.localStorage.getItem(STORAGE_KEY);
		if (isAccountingDetailMode(stored)) mode = stored;
	});
</script>

<div class={['inline-flex items-center gap-1 rounded-full border border-border bg-card p-1', className]} data-testid={testid} data-mode={mode}>
	<span class="sr-only">Accounting detail level</span>
	<Button
		variant={mode === 'simple' ? 'secondary' : 'ghost'}
		size="sm"
		class="h-8 rounded-full px-3"
		aria-pressed={mode === 'simple'}
		aria-label="Use simple accounting detail"
		onclick={() => setMode('simple')}
		data-testid={`${testid}-simple`}
	>
		Simple
	</Button>
	<Button
		variant={mode === 'advanced' ? 'secondary' : 'ghost'}
		size="sm"
		class="h-8 rounded-full px-3"
		aria-pressed={mode === 'advanced'}
		aria-label="Use advanced accounting detail"
		onclick={() => setMode('advanced')}
		data-testid={`${testid}-advanced`}
	>
		Advanced
	</Button>
</div>

{#if children}
	{@render children()}
{/if}
