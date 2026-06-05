<script lang="ts">
	import { Building, ArrowUpRight, BookOpen } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { getCurrentUser } from '$lib/stores/auth.svelte';

	let { children }: { children: import('svelte').Snippet } = $props();

	// Docs are public, but a logged-in user reading them should get a link back
	// into the app rather than a "sign in" CTA.
	let user = $derived(getCurrentUser());
</script>

<!-- Own scroll container: html/body are overflow:hidden globally (the app shell manages its
     own scroll), so this public docs surface must scroll itself. -->
<div class="flex h-dvh flex-col overflow-y-auto bg-background text-foreground">
	<!-- Branded public header -->
	<header
		class="sticky top-0 z-40 border-b border-border/60 bg-background/80 backdrop-blur supports-[backdrop-filter]:bg-background/60"
	>
		<div class="mx-auto flex h-16 max-w-6xl items-center justify-between px-5 sm:px-8">
			<div class="flex items-center gap-2">
				<a href="/welcome" class="flex items-center gap-2">
					<span
						class="flex h-8 w-8 items-center justify-center rounded-lg bg-primary/10 text-primary ring-1 ring-inset ring-primary/20"
					>
						<Building class="h-4 w-4" />
					</span>
					<span class="text-base font-semibold tracking-tight">Rental Command</span>
				</a>
				<span class="hidden text-border sm:inline">/</span>
				<a
					href="/docs"
					class="hidden items-center gap-1.5 text-sm font-medium text-muted-foreground transition-colors hover:text-foreground sm:inline-flex"
				>
					<BookOpen class="h-4 w-4" />
					Docs
				</a>
			</div>
			<div class="flex items-center gap-2">
				{#if user}
					<Button href="/" variant="outline" size="sm">
						Back to app
						<ArrowUpRight class="h-4 w-4" />
					</Button>
				{:else}
					<Button href="/login" variant="ghost" size="sm" class="hidden sm:inline-flex">Sign in</Button>
					<Button href="/register" size="sm">
						Get started
						<ArrowUpRight class="h-4 w-4" />
					</Button>
				{/if}
			</div>
		</div>
	</header>

	<div class="flex-1">
		{@render children()}
	</div>

	<!-- Footer -->
	<footer class="border-t border-border/60">
		<div
			class="mx-auto flex max-w-6xl flex-col items-center justify-between gap-4 px-5 py-8 text-sm text-muted-foreground sm:flex-row sm:px-8"
		>
			<div class="flex items-center gap-2">
				<span class="flex h-7 w-7 items-center justify-center rounded-lg bg-primary/10 text-primary">
					<Building class="h-4 w-4" />
				</span>
				<span class="font-medium text-foreground">Rental Command</span>
			</div>
			<div class="flex items-center gap-5">
				<a href="/docs" class="hover:text-foreground">Docs</a>
				<a href="/welcome" class="hover:text-foreground">Home</a>
				<a href="/login" class="hover:text-foreground">Sign in</a>
			</div>
			<span class="text-xs">© {new Date().getFullYear()} Rental Command</span>
		</div>
	</footer>
</div>
