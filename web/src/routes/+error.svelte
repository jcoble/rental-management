<!--
  App-wide error / 404 page. Rendered inside the root +layout (so M3 theme tokens,
  fonts and light/dark all apply) but outside the (protected) AppShell, so it has no
  sidebar — appropriate for a not-found/unknown-route landing. Gives a non-technical
  user a clear, friendly message and a one-tap way back to the dashboard instead of
  SvelteKit's bare unstyled "404 Not Found".
-->
<script lang="ts">
	import { page } from '$app/state';
	import { Home, Compass, AlertTriangle } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';

	const isNotFound = $derived(page.status === 404);
	const title = $derived(isNotFound ? "We can't find that page" : 'Something went wrong');
	const message = $derived(
		isNotFound
			? "The page you're looking for doesn't exist or may have moved. Let's get you back on track."
			: (page.error?.message ?? 'An unexpected error occurred. Please try again in a moment.')
	);
</script>

<svelte:head>
	<title>{isNotFound ? 'Page not found' : 'Error'} - Rental Command</title>
</svelte:head>

<div
	class="flex min-h-svh flex-col items-center justify-center bg-background px-5 py-16 text-center text-foreground"
	data-testid="app-error-page"
>
	<div
		class="flex h-16 w-16 items-center justify-center rounded-2xl bg-secondary text-secondary-foreground ring-1 ring-inset ring-border"
	>
		{#if isNotFound}
			<Compass class="h-8 w-8 text-muted-foreground" />
		{:else}
			<AlertTriangle class="h-8 w-8 text-destructive" />
		{/if}
	</div>

	<p class="mt-6 text-sm font-medium uppercase tracking-wide text-muted-foreground">
		{isNotFound ? 'Error 404' : `Error ${page.status}`}
	</p>
	<h1 class="mt-1 text-3xl font-semibold tracking-tight">{title}</h1>
	<p class="mt-3 max-w-md text-sm text-muted-foreground">{message}</p>

	<div class="mt-8 flex flex-wrap items-center justify-center gap-3">
		<Button href="/" data-testid="app-error-home">
			<Home class="h-4 w-4" />
			Back to dashboard
		</Button>
	</div>
</div>
