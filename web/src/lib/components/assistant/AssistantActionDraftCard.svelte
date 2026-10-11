<script lang="ts">
	import { CheckCircle2, ShieldAlert } from '@lucide/svelte';
	import type {
		AssistantActionDraft,
		AssistantActionDraftResponse
	} from '$lib/api/endpoints/ai';
	import {
		assistantActionFieldLabel,
		formatAssistantMoney
	} from '$lib/assistant/actions';
	import { formatStatusLabel } from '$lib/utils/status-labels';

	let {
		draft,
		status,
		missingFields = [],
		writeModeEnabled,
		isExecuting = false,
		note = null,
		onConfirm
	}: {
		draft?: AssistantActionDraft | null;
		status?: AssistantActionDraftResponse['status'] | null;
		missingFields?: string[];
		writeModeEnabled: boolean;
		isExecuting?: boolean;
		note?: string | null;
		onConfirm?: () => void;
	} = $props();

	const expense = $derived(draft?.expense ?? null);
	const canConfirm = $derived(
		!!draft && status === 'DraftReady' && writeModeEnabled && !isExecuting && !!onConfirm
	);
	const showConfirm = $derived(!!draft && status !== 'Created');
</script>

{#if expense}
	<div
		class="mt-2 rounded-lg border border-border bg-muted/30 p-3 text-sm"
		data-testid="assistant-action-draft"
	>
		<div class="flex items-start justify-between gap-3">
			<div class="min-w-0">
				<p class="font-medium">Confirm expense draft</p>
				<p class="mt-0.5 text-xs text-muted-foreground">{draft?.summary}</p>
			</div>
			<span
				class="inline-flex shrink-0 items-center gap-1 rounded-full border px-2 py-0.5 text-xs {status === 'Created'
					? 'border-emerald-500/40 bg-emerald-500/10 text-emerald-700 dark:text-emerald-300'
					: 'border-amber-500/40 bg-amber-500/10 text-amber-700 dark:text-amber-300'}"
			>
				{#if status === 'Created'}
					<CheckCircle2 class="h-3.5 w-3.5" />
					Created
				{:else}
					<ShieldAlert class="h-3.5 w-3.5" />
					Needs confirmation
				{/if}
			</span>
		</div>

		<div class="mt-3 grid gap-2 sm:grid-cols-2">
			<div>
				<p class="text-[11px] uppercase tracking-wide text-muted-foreground">Amount</p>
				<p class="font-medium tabular-nums">{formatAssistantMoney(expense.amount)}</p>
			</div>
			<div>
				<p class="text-[11px] uppercase tracking-wide text-muted-foreground">Property</p>
				<p class="font-medium">{expense.propertyName ?? 'No property selected'}</p>
			</div>
			<div>
				<p class="text-[11px] uppercase tracking-wide text-muted-foreground">Category</p>
				<p class="font-medium">{expense.category}</p>
			</div>
			<div>
				<p class="text-[11px] uppercase tracking-wide text-muted-foreground">Status</p>
				<p class="font-medium">{formatStatusLabel(expense.status)}</p>
			</div>
			<div class="sm:col-span-2">
				<p class="text-[11px] uppercase tracking-wide text-muted-foreground">Description</p>
				<p class="font-medium">{expense.description || 'No description yet'}</p>
			</div>
		</div>

		{#if missingFields.length > 0}
			<p class="mt-3 text-xs text-destructive" data-testid="assistant-action-missing-fields">
				Add {missingFields.map(assistantActionFieldLabel).join(', ')} before this can be created.
			</p>
		{/if}

		{#if status === 'WriteModeRequired'}
			<p class="mt-3 text-xs text-muted-foreground" data-testid="assistant-action-write-mode-note">
				Turn on Action mode before confirming. Reads stay available when Action mode is off.
			</p>
		{/if}

		{#if note}
			<p class="mt-3 text-xs text-muted-foreground" data-testid="assistant-action-note">
				{note}
			</p>
		{/if}

		{#if showConfirm}
			<div class="mt-3 flex justify-end">
				<button
					type="button"
					class="inline-flex items-center justify-center rounded-md bg-primary px-3 py-1.5 text-xs font-medium text-primary-foreground transition-colors hover:bg-primary/90 disabled:pointer-events-none disabled:opacity-50"
					disabled={!canConfirm}
					onclick={onConfirm}
					data-testid="assistant-action-confirm"
				>
					{isExecuting ? 'Creating...' : 'Confirm & create expense'}
				</button>
			</div>
		{/if}
	</div>
{:else if note}
	<p class="mt-1 px-1 text-xs text-muted-foreground" data-testid="assistant-action-note">
		{note}
	</p>
{/if}
