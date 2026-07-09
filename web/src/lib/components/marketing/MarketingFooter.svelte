<script lang="ts">
	import BrandMark from '$lib/components/BrandMark.svelte';

	/** Shared marketing footer (landing + features + other public marketing pages). */
	const year = new Date().getFullYear();
</script>

<footer class="border-t border-border/60">
	<div
		class="mx-auto flex max-w-6xl flex-col items-center justify-between gap-4 px-5 py-9 text-sm text-muted-foreground sm:flex-row sm:px-8"
	>
		<div class="flex items-center gap-2">
			<BrandMark class="h-7 w-7 shadow-sm" />
			<span class="font-medium text-foreground">Rental Command</span>
		</div>
		<div class="flex flex-wrap items-center justify-center gap-x-6 gap-y-2">
			<a href="/features" class="transition-colors hover:text-foreground">Features</a>
			<a href="/docs" class="transition-colors hover:text-foreground">Docs</a>
			<a href="/login" class="transition-colors hover:text-foreground">Sign in</a>
			<a href="/register" class="transition-colors hover:text-foreground">Create account</a>
		</div>
		<span class="text-xs">© {year} Rental Command</span>
	</div>
</footer>
