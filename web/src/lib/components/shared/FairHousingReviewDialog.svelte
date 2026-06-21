<script lang="ts">
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { AlertTriangle, ShieldCheck } from '@lucide/svelte';
	import type { FairHousingConcern } from '$lib/api/client';

	let {
		open = false,
		detail = '',
		concerns = [],
		busy = false,
		testid = 'fair-housing-review',
		onedit,
		onsendanyway,
	}: {
		/** Whether the dialog is shown. */
		open?: boolean;
		/** The reviewer's summary sentence (ProblemDetails.detail). */
		detail?: string;
		/** Each flagged phrase + the reason it was flagged. */
		concerns?: FairHousingConcern[];
		/** Disables both actions while the override re-send is in flight. */
		busy?: boolean;
		testid?: string;
		/** "Edit" — dismiss so the landlord can revise the copy. */
		onedit?: () => void;
		/** "Send anyway" — re-submit with acknowledgedFairHousingReview: true. */
		onsendanyway?: () => void;
	} = $props();
</script>

<Dialog.Root {open} onOpenChange={(v) => { if (!v) onedit?.(); }}>
	<Dialog.Content class="max-w-xl" data-testid="{testid}-dialog">
		<Dialog.Header>
			<Dialog.Title class="flex items-center gap-2 text-[var(--warning)]">
				<ShieldCheck class="h-5 w-5 shrink-0" />
				Fair Housing review
			</Dialog.Title>
			{#if detail}
				<Dialog.Description data-testid="{testid}-detail">{detail}</Dialog.Description>
			{/if}
		</Dialog.Header>

		<div class="m3-warning-surface space-y-2 rounded-md p-3 text-sm" data-testid="{testid}-concerns">
			<p class="flex items-center gap-2 font-medium text-[var(--warning)]">
				<AlertTriangle class="h-4 w-4 shrink-0" />
				Flagged wording
			</p>
			<ul class="space-y-1.5">
				{#each concerns as c (c.phrase + c.concern)}
					<li class="text-muted-foreground" data-testid="{testid}-concern">
						{#if c.phrase}<span class="font-medium text-foreground">&ldquo;{c.phrase}&rdquo;</span>{/if}
						{#if c.phrase && c.concern}&nbsp;&mdash;&nbsp;{/if}
						{c.concern}
					</li>
				{/each}
			</ul>
		</div>

		<Dialog.Footer>
			<Button
				variant="outline"
				data-testid="{testid}-edit"
				onclick={onedit}
				disabled={busy}
			>
				Edit
			</Button>
			<Button
				data-testid="{testid}-send-anyway"
				onclick={onsendanyway}
				disabled={busy}
			>
				{busy ? 'Sending…' : "I've reviewed this — send anyway"}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
