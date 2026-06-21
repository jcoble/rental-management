<script lang="ts">
	import { Building, ArrowUpRight, BookOpen } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { getCurrentUser } from '$lib/stores/auth.svelte';
	import { page } from '$app/state';
	import type { LayoutData } from './$types';

	let { data, children }: { data: LayoutData; children: import('svelte').Snippet } = $props();

	// Docs are public, but a logged-in user reading them should get a link back
	// into the app rather than a "sign in" CTA.
	let user = $derived(getCurrentUser());

	// Persistent left navigation: every category + article, loaded once by
	// +layout.server.ts (fail-soft to an empty index). The current article is highlighted.
	const categories = $derived(data.index?.categories ?? []);
	const currentSlug = $derived(page.params.slug ?? '');
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

	<!-- 3-pane docs: persistent left nav (categories + articles) | content. The article page renders
	     its own "on this page" rail on the right. The left nav is sticky and hidden below lg. -->
	<div class="docs-layout flex-1">
		<aside class="docs-nav" aria-label="Documentation navigation">
			<div class="docs-nav-inner">
				{#each categories as cat (cat.category)}
					<div class="docs-nav-cat">
						<p class="docs-nav-cat-title">{cat.category}</p>
						<ul>
							{#each cat.articles as a (a.slug)}
								<li>
									<a
										href="/docs/{a.slug}"
										class="docs-nav-link"
										class:active={a.slug === currentSlug}
										aria-current={a.slug === currentSlug ? 'page' : undefined}
									>
										{a.title}
									</a>
								</li>
							{/each}
						</ul>
					</div>
				{/each}
			</div>
		</aside>

		<div class="docs-main">
			{@render children()}
		</div>
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

<style>
	.docs-layout {
		width: 100%;
		max-width: 80rem;
		margin: 0 auto;
		padding: 0 1.25rem;
		display: grid;
		grid-template-columns: 1fr;
	}
	@media (min-width: 1024px) {
		.docs-layout {
			grid-template-columns: 14rem minmax(0, 1fr);
			gap: 2.5rem;
		}
	}

	.docs-nav {
		display: none;
	}
	@media (min-width: 1024px) {
		.docs-nav {
			display: block;
		}
	}
	.docs-nav-inner {
		position: sticky;
		top: 1.5rem;
		max-height: calc(100dvh - 3rem);
		overflow-y: auto;
		padding: 2rem 0;
	}
	.docs-nav-cat {
		margin-bottom: 1.4rem;
	}
	.docs-nav-cat-title {
		font-size: 0.72rem;
		font-weight: 700;
		text-transform: uppercase;
		letter-spacing: 0.06em;
		color: var(--muted-foreground);
		margin: 0 0 0.45rem;
		padding-left: 0.75rem;
	}
	.docs-nav ul {
		list-style: none;
		margin: 0;
		padding: 0;
	}
	.docs-nav-link {
		display: block;
		padding: 0.35rem 0.75rem;
		font-size: 0.875rem;
		line-height: 1.35;
		color: var(--muted-foreground);
		border-left: 2px solid transparent;
		border-radius: 0 0.4rem 0.4rem 0;
		transition:
			color 0.15s ease,
			background 0.15s ease,
			border-color 0.15s ease;
	}
	.docs-nav-link:hover {
		color: var(--foreground);
		background: color-mix(in srgb, var(--primary) 8%, transparent);
	}
	.docs-nav-link.active {
		color: var(--primary);
		font-weight: 600;
		border-left-color: var(--primary);
		background: color-mix(in srgb, var(--primary) 10%, transparent);
	}

	.docs-main {
		min-width: 0;
	}
</style>
