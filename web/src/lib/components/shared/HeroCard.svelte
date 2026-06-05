<script lang="ts" module>
	/** Context tone — maps onto the app's semantic tokens so the wash reads
	 *  correctly in both light and dark themes. */
	export type HeroTone = 'primary' | 'success' | 'warning' | 'destructive' | 'muted';
</script>

<script lang="ts">
	import type { Snippet } from 'svelte';
	import * as Card from '$lib/components/ui/card';
	import { cn } from '$lib/utils.js';

	let {
		tone = 'primary',
		testid,
		class: className = '',
		contentClass = '',
		children,
	}: {
		/** Context tint — keyed to meaning (status/severity), not decoration. */
		tone?: HeroTone;
		testid?: string;
		class?: string;
		/** Extra classes for the inner Card.Content (e.g. flex layout). */
		contentClass?: string;
		children: Snippet;
	} = $props();

	// The softened recipe: a three-stop diagonal wash that melts into the card.
	// - Start a touch stronger (/15) than the old /10 so the tint reads.
	// - A via-/5 mid-stop eases the falloff so there's no hard band.
	// - Fade to transparent — the wrapper sits INSIDE Card.Root, so "transparent"
	//   reveals the card surface (not the page bg), keeping it seamless + themed.
	// Class strings are fully literal so Tailwind's JIT keeps every variant.
	const gradientByTone: Record<HeroTone, string> = {
		primary: 'bg-gradient-to-br from-primary/15 via-primary/5 to-transparent',
		success: 'bg-gradient-to-br from-success/15 via-success/5 to-transparent',
		warning: 'bg-gradient-to-br from-warning/15 via-warning/5 to-transparent',
		destructive: 'bg-gradient-to-br from-destructive/15 via-destructive/5 to-transparent',
		muted: 'bg-gradient-to-br from-muted-foreground/12 via-muted-foreground/5 to-transparent',
	};
</script>

<Card.Root class={cn('overflow-hidden', className)} data-testid={testid}>
	<div class={gradientByTone[tone]}>
		<Card.Content class={cn('p-6', contentClass)}>
			{@render children()}
		</Card.Content>
	</div>
</Card.Root>
