<script lang="ts">
	import { X, Lightbulb } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { isPageSeen, markPageSeen } from '$lib/stores/guidanceState';

	type Props = {
		pageKey: string;
		title: string;
		description: string;
		learnMoreUrl?: string;
	};

	let { pageKey, title, description, learnMoreUrl }: Props = $props();

	let dismissed = $derived.by(() => isPageSeen(pageKey) ? true : manualDismissed);
	let manualDismissed = $state(false);

	function dismiss() {
		markPageSeen(pageKey);
		manualDismissed = true;
	}
</script>

{#if !dismissed}
	<div
		class="relative flex items-start gap-3 rounded-lg border border-border bg-muted/50 p-4"
		role="region"
		aria-label="Page introduction"
		data-testid="page-intro-{pageKey}"
	>
		<Lightbulb class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
		<div class="flex-1 min-w-0">
			<p class="text-sm font-medium text-foreground">{title}</p>
			<p class="mt-0.5 text-sm text-muted-foreground">{description}</p>
			{#if learnMoreUrl}
				<a
					href={learnMoreUrl}
					class="mt-1 inline-block text-xs font-medium text-primary underline hover:text-primary/80"
				>
					Learn more
				</a>
			{/if}
		</div>
		<Button
			variant="ghost"
			size="sm"
			class="h-6 w-6 shrink-0 p-0 text-muted-foreground hover:text-foreground"
			onclick={dismiss}
			aria-label="Dismiss introduction"
			data-testid="page-intro-dismiss"
		>
			<X class="h-3.5 w-3.5" />
		</Button>
	</div>
{/if}
