<script lang="ts">
	import { Search, BookOpen, FileText, ArrowRight, Menu, X } from '@lucide/svelte';
	import { Input } from '$lib/components/ui/input';
	import type { DocArticleSummary } from '$lib/api/endpoints/docs';
	import type { PageData } from './$types';

	let { data }: { data: PageData } = $props();

	const categories = $derived(data.index.categories ?? []);

	// Flat article list for searching.
	const allArticles = $derived(
		categories.flatMap((c) =>
			c.articles.map((a) => ({ ...a, category: a.category || c.category }))
		)
	);

	let query = $state('');
	let mobileNavOpen = $state(false);

	function matches(article: DocArticleSummary, q: string): boolean {
		const haystack = [article.title, article.summary, article.category].join(' ').toLowerCase();
		return haystack.includes(q);
	}

	// Client-side filtered categories (by title/summary/category). Empty categories drop out.
	const filteredCategories = $derived.by(() => {
		const q = query.trim().toLowerCase();
		if (!q) return categories;
		return categories
			.map((c) => ({ ...c, articles: c.articles.filter((a) => matches(a, q)) }))
			.filter((c) => c.articles.length > 0);
	});

	const totalArticles = $derived(allArticles.length);
	const matchCount = $derived(filteredCategories.reduce((n, c) => n + c.articles.length, 0));
</script>

<svelte:head>
	<title>Documentation — Rental Command</title>
	<meta
		name="description"
		content="Guides and help for Rental Command — scanning documents, managing leases, tenants, money, maintenance, and the AI assistant."
	/>
</svelte:head>

<div class="mx-auto max-w-6xl px-5 py-10 sm:px-8 lg:py-14" data-testid="docs-page">
	<!-- Page heading -->
	<div class="mb-8">
		<span
			class="inline-flex items-center gap-2 rounded-full border border-primary/20 bg-primary/10 px-3 py-1 text-xs font-medium text-primary"
		>
			<BookOpen class="h-3.5 w-3.5" />
			Documentation
		</span>
		<h1 class="mt-4 text-3xl font-semibold tracking-tight sm:text-4xl">How can we help?</h1>
		<p class="mt-3 max-w-xl text-muted-foreground">
			Guides for getting the most out of Rental Command — from scanning your first document to
			managing leases, money, and maintenance.
		</p>

		<!-- Search -->
		<div class="relative mt-6 max-w-md">
			<Search
				class="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
			/>
			<Input
				bind:value={query}
				type="search"
				placeholder="Search the docs…"
				class="h-11 pl-9"
				aria-label="Search documentation"
				data-testid="docs-search"
			/>
		</div>
		{#if query.trim()}
			<p class="mt-2 text-xs text-muted-foreground" data-testid="docs-search-count">
				{matchCount} of {totalArticles} article{totalArticles === 1 ? '' : 's'} match “{query.trim()}”
			</p>
		{/if}
	</div>

	<div class="grid gap-8 lg:grid-cols-[16rem_1fr]">
		<!-- Sidebar (categories → articles) -->
		<aside class="lg:sticky lg:top-24 lg:self-start">
			<!-- Mobile toggle -->
			<button
				type="button"
				class="mb-3 flex w-full items-center justify-between rounded-lg border border-border bg-card px-4 py-2.5 text-sm font-medium lg:hidden"
				onclick={() => (mobileNavOpen = !mobileNavOpen)}
				aria-expanded={mobileNavOpen}
			>
				<span class="flex items-center gap-2">
					{#if mobileNavOpen}<X class="h-4 w-4" />{:else}<Menu class="h-4 w-4" />{/if}
					Browse topics
				</span>
			</button>

			<nav
				class="{mobileNavOpen ? 'block' : 'hidden'} space-y-5 lg:block"
				data-testid="docs-sidebar"
			>
				{#each filteredCategories as cat (cat.category)}
					<div>
						<p
							class="mb-1.5 px-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground/70"
						>
							{cat.category}
						</p>
						<ul class="space-y-0.5">
							{#each cat.articles as article (article.slug)}
								<li>
									<a
										href="/docs/{article.slug}"
										class="block rounded-md px-2 py-1.5 text-sm text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
										data-testid="docs-sidebar-link-{article.slug}"
									>
										{article.title}
									</a>
								</li>
							{/each}
						</ul>
					</div>
				{:else}
					<p class="px-2 text-sm text-muted-foreground">No topics found.</p>
				{/each}
			</nav>
		</aside>

		<!-- Main: category/article cards -->
		<div class="min-w-0">
			{#if filteredCategories.length === 0}
				<div
					class="flex flex-col items-center justify-center rounded-2xl border border-dashed border-border bg-card/40 py-16 text-center"
					data-testid="docs-empty"
				>
					<Search class="h-8 w-8 text-muted-foreground" />
					<p class="mt-3 text-sm font-medium">No articles match “{query.trim()}”</p>
					<p class="mt-1 text-xs text-muted-foreground">Try a different word or clear the search.</p>
				</div>
			{:else}
				<div class="space-y-10">
					{#each filteredCategories as cat (cat.category)}
						<section data-testid="docs-category-{cat.category}">
							<h2 class="text-lg font-semibold tracking-tight">{cat.category}</h2>
							<div class="mt-4 grid gap-4 sm:grid-cols-2">
								{#each cat.articles as article (article.slug)}
									<a
										href="/docs/{article.slug}"
										class="group flex flex-col rounded-2xl border border-border bg-card p-5 transition-colors hover:border-primary/40"
										data-testid="docs-article-card-{article.slug}"
									>
										<div
											class="flex h-10 w-10 items-center justify-center rounded-xl bg-secondary ring-1 ring-inset ring-border transition-colors group-hover:bg-primary/10"
										>
											<FileText
												class="h-5 w-5 text-muted-foreground transition-colors group-hover:text-primary"
											/>
										</div>
										<h3 class="mt-4 text-base font-semibold tracking-tight">{article.title}</h3>
										{#if article.summary}
											<p class="mt-1.5 line-clamp-2 text-sm leading-relaxed text-muted-foreground">
												{article.summary}
											</p>
										{/if}
										<span
											class="mt-3 inline-flex items-center gap-1 text-sm font-medium text-primary opacity-0 transition-opacity group-hover:opacity-100"
										>
											Read <ArrowRight class="h-3.5 w-3.5" />
										</span>
									</a>
								{/each}
							</div>
						</section>
					{/each}
				</div>
			{/if}
		</div>
	</div>
</div>
