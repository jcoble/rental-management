<script lang="ts">
	import { ChevronLeft, ChevronRight } from '@lucide/svelte';

	let {
		skip = $bindable(0),
		take = 20,
		count = 0,
		hasNext: explicitHasNext = undefined,
		testid = 'pagination',
	}: {
		/** Current offset (two-way bound). */
		skip?: number;
		/** Page size. */
		take?: number;
		/** Number of records returned for the current page. */
		count?: number;
		/** Explicit next-page signal when a loader overfetches by one row. */
		hasNext?: boolean;
		testid?: string;
	} = $props();

	const page = $derived(Math.floor(skip / take) + 1);
	// Without an explicit overfetch signal, a full page only implies a probable next page.
	const canNext = $derived(explicitHasNext ?? count >= take);
	const hasPrev = $derived(skip > 0);

	function prev() {
		if (hasPrev) skip = Math.max(0, skip - take);
	}
	function next() {
		if (canNext) skip = skip + take;
	}
</script>

<div class="flex items-center justify-between gap-3 text-sm" data-testid="{testid}">
	<span class="text-muted-foreground" data-testid="{testid}-status">
		Page {page}{count ? ` · ${count} shown` : ''}
	</span>
	<div class="flex items-center gap-2">
		<button
			type="button"
			data-testid="{testid}-prev"
			class="inline-flex items-center gap-1 rounded-md border border-border px-2.5 py-1.5 text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground disabled:opacity-40 disabled:pointer-events-none"
			onclick={prev}
			disabled={!hasPrev}
		>
			<ChevronLeft class="h-4 w-4" /> Prev
		</button>
		<button
			type="button"
			data-testid="{testid}-next"
			class="inline-flex items-center gap-1 rounded-md border border-border px-2.5 py-1.5 text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground disabled:opacity-40 disabled:pointer-events-none"
			onclick={next}
			disabled={!canNext}
		>
			Next <ChevronRight class="h-4 w-4" />
		</button>
	</div>
</div>
