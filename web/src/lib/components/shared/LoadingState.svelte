<script lang="ts">
	type LoadingVariant = 'page' | 'section' | 'spinner';

	let {
		label,
		variant = 'section',
		testid,
	}: {
		label: string;
		variant?: LoadingVariant;
		testid?: string;
	} = $props();
</script>

<div
	class:loading-page={variant === 'page'}
	class:loading-section={variant === 'section'}
	class:loading-spinner={variant === 'spinner'}
	role="status"
	aria-live="polite"
	aria-busy="true"
	aria-label={label}
	data-testid={testid}
>
	<span class="sr-only">{label}</span>

	{#if variant === 'page'}
		<div class="loading-pulse space-y-5" aria-hidden="true">
			<div class="space-y-3">
				<div class="h-7 w-48 rounded-md bg-muted"></div>
				<div class="h-4 w-72 max-w-full rounded bg-muted"></div>
			</div>
			<div class="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
				{#each Array(4) as _}
					<div class="h-32 rounded-xl border border-border/60 bg-card"></div>
				{/each}
			</div>
			<div class="space-y-3 rounded-xl border border-border/60 bg-card p-5">
				<div class="h-5 w-40 rounded bg-muted"></div>
				<div class="h-16 rounded-lg bg-muted"></div>
				<div class="h-16 rounded-lg bg-muted"></div>
				<div class="h-16 rounded-lg bg-muted"></div>
			</div>
		</div>
	{:else if variant === 'section'}
		<div class="loading-pulse space-y-3" aria-hidden="true">
			<div class="h-5 w-40 max-w-full rounded bg-muted"></div>
			<div class="h-14 rounded-lg bg-muted"></div>
			<div class="h-14 rounded-lg bg-muted"></div>
			<div class="h-14 w-5/6 rounded-lg bg-muted"></div>
		</div>
	{:else}
		<div class="flex items-center justify-center gap-3 text-sm text-muted-foreground">
			<span class="loading-spinner-ring" aria-hidden="true"></span>
			<span>{label}</span>
		</div>
	{/if}
</div>

<style>
	.loading-page {
		min-height: 32rem;
	}

	.loading-section {
		min-height: 13rem;
		border: 1px solid var(--border);
		border-radius: 0.75rem;
		background: var(--card);
		padding: 1.25rem;
	}

	.loading-spinner {
		display: grid;
		min-height: 8rem;
		place-items: center;
	}

	.loading-spinner-ring {
		width: 1.25rem;
		height: 1.25rem;
		border: 2px solid var(--muted);
		border-top-color: var(--primary);
		border-radius: 9999px;
		animation: loading-spin 700ms linear infinite;
	}

	.loading-pulse {
		animation: loading-pulse 1.6s cubic-bezier(0.4, 0, 0.6, 1) infinite;
	}

	@keyframes loading-pulse {
		0%,
		100% {
			opacity: 1;
		}
		50% {
			opacity: 0.52;
		}
	}

	@keyframes loading-spin {
		to {
			transform: rotate(360deg);
		}
	}

	@media (prefers-reduced-motion: reduce) {
		.loading-pulse,
		.loading-spinner-ring {
			animation: none;
		}
	}
</style>
