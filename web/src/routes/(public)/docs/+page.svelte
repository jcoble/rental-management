<script lang="ts">
	import {
		Search,
		BookOpen,
		ArrowRight,
		Sparkles,
		Rocket,
		FileText,
		ScanLine,
		Users,
		Banknote,
		Wrench,
		Bot,
		Building2,
		DoorOpen,
		Settings
	} from '@lucide/svelte';
	import { Input } from '$lib/components/ui/input';
	import type { DocArticleSummary, DocCategory } from '$lib/api/endpoints/docs';
	import type { PageData } from './$types';

	let { data }: { data: PageData } = $props();

	const categories = $derived(data.index.categories ?? []);

	// Per-category icon + accent, keyed by the category *name* the API returns
	// (RC has no category ids). Mirrors how EdiPlatform colocates its icon/accent
	// maps on the docs index. Unknown categories fall back to a neutral treatment.
	const iconMap: Record<string, typeof BookOpen> = {
		'Getting Started': Rocket,
		'Scan & Intake': ScanLine,
		'Tenants & Leases': Users,
		Money: Banknote,
		Operations: Wrench,
		'AI Assistant': Bot,
		'Properties & Units': Building2,
		'Tenant Portal': DoorOpen,
		Settings: Settings
	};

	const accentMap: Record<string, string> = {
		'Getting Started': 'text-emerald-400 bg-emerald-500/10 group-hover:bg-emerald-500/15',
		'Scan & Intake': 'text-blue-400 bg-blue-500/10 group-hover:bg-blue-500/15',
		'Tenants & Leases': 'text-violet-400 bg-violet-500/10 group-hover:bg-violet-500/15',
		Money: 'text-green-400 bg-green-500/10 group-hover:bg-green-500/15',
		Operations: 'text-amber-400 bg-amber-500/10 group-hover:bg-amber-500/15',
		'AI Assistant': 'text-cyan-400 bg-cyan-500/10 group-hover:bg-cyan-500/15',
		'Properties & Units': 'text-orange-400 bg-orange-500/10 group-hover:bg-orange-500/15',
		'Tenant Portal': 'text-pink-400 bg-pink-500/10 group-hover:bg-pink-500/15',
		Settings: 'text-slate-300 bg-slate-500/10 group-hover:bg-slate-500/15'
	};

	const FALLBACK_ACCENT = 'text-muted-foreground bg-secondary group-hover:bg-secondary';

	// Flat article list for searching.
	const allArticles = $derived(
		categories.flatMap((c) => c.articles.map((a) => ({ ...a, category: a.category || c.category })))
	);

	let query = $state('');

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
	const searching = $derived(query.trim().length > 0);

	// "Start here" target: the first Getting Started article, else the very first article.
	const startArticle = $derived.by<DocArticleSummary | null>(() => {
		const gettingStarted = categories.find((c) => c.category === 'Getting Started');
		return gettingStarted?.articles[0] ?? allArticles[0] ?? null;
	});

	function categoryHref(cat: DocCategory): string {
		return cat.articles[0] ? `/docs/${cat.articles[0].slug}` : '/docs';
	}
</script>

<svelte:head>
	<title>Documentation — Rental Command</title>
	<meta
		name="description"
		content="Guides and help for Rental Command — scanning documents, managing leases, tenants, money, maintenance, and the AI assistant."
	/>
</svelte:head>

<div class="mx-auto max-w-5xl px-5 py-10 sm:px-8 lg:py-14" data-testid="docs-page">
	<!-- Hero -->
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
		{#if searching}
			<p class="mt-2 text-xs text-muted-foreground" data-testid="docs-search-count">
				{matchCount} of {totalArticles} article{totalArticles === 1 ? '' : 's'} match “{query.trim()}”
			</p>
		{/if}
	</div>

	{#if searching && filteredCategories.length === 0}
		<!-- Empty search state -->
		<div
			class="flex flex-col items-center justify-center rounded-2xl border border-dashed border-border bg-card/40 py-16 text-center"
			data-testid="docs-empty"
		>
			<Search class="h-8 w-8 text-muted-foreground" />
			<p class="mt-3 text-sm font-medium">No articles match “{query.trim()}”</p>
			<p class="mt-1 text-xs text-muted-foreground">Try a different word or clear the search.</p>
		</div>
	{:else}
		<!-- Start here (hidden while searching — it's a landing affordance, not a result) -->
		{#if !searching && startArticle}
			<a
				href="/docs/{startArticle.slug}"
				class="group mb-8 block rounded-2xl border border-primary/20 bg-gradient-to-br from-primary/5 to-primary/10 p-6 transition-all hover:border-primary/35 hover:shadow-lg hover:shadow-primary/5"
				data-testid="docs-start-here"
			>
				<div class="flex items-start gap-4">
					<div class="rounded-xl bg-primary/15 p-3 ring-1 ring-inset ring-primary/20">
						<Sparkles class="h-6 w-6 text-primary" />
					</div>
					<div class="flex-1">
						<h2 class="text-lg font-semibold tracking-tight">New here? Start with the basics</h2>
						<p class="mt-1.5 text-sm leading-relaxed text-muted-foreground">
							Learn what Rental Command is, set up your portfolio, and see how scanning turns a
							document into a record — no typing.
						</p>
						<span
							class="mt-3 inline-flex items-center gap-1.5 text-sm font-medium text-primary transition-all group-hover:gap-2.5"
						>
							{startArticle.title}
							<ArrowRight class="h-4 w-4" />
						</span>
					</div>
				</div>
			</a>
		{/if}

		<!-- Category grid: one curated card per topic (no second full TOC). -->
		<div class="grid gap-4 sm:grid-cols-2">
			{#each filteredCategories as cat (cat.category)}
				{@const Icon = iconMap[cat.category] || FileText}
				{@const accent = accentMap[cat.category] || FALLBACK_ACCENT}
				<section
					class="flex flex-col rounded-2xl border border-border bg-card p-5 transition-colors hover:border-primary/30"
					data-testid="docs-category-{cat.category}"
				>
					<a
						href={categoryHref(cat)}
						class="group flex items-start gap-3.5"
						data-testid="docs-category-link-{cat.category}"
					>
						<div class="rounded-xl p-2.5 ring-1 ring-inset ring-border transition-colors {accent}">
							<Icon class="h-5 w-5" />
						</div>
						<div class="min-w-0 flex-1">
							<h2 class="font-semibold tracking-tight transition-colors group-hover:text-primary">
								{cat.category}
							</h2>
							<p class="mt-0.5 text-xs text-muted-foreground">
								{cat.articles.length} article{cat.articles.length === 1 ? '' : 's'}
							</p>
						</div>
						<ArrowRight
							class="mt-1 h-4 w-4 shrink-0 text-muted-foreground/50 transition-all group-hover:translate-x-0.5 group-hover:text-primary"
						/>
					</a>

					<ul class="mt-4 space-y-0.5 border-t border-border/60 pt-3">
						{#each cat.articles.slice(0, 4) as article (article.slug)}
							<li>
								<a
									href="/docs/{article.slug}"
									class="block rounded-md px-2 py-1.5 text-sm text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
									data-testid="docs-article-link-{article.slug}"
								>
									{article.title}
								</a>
							</li>
						{/each}
						{#if cat.articles.length > 4}
							<li>
								<a
									href={categoryHref(cat)}
									class="block rounded-md px-2 py-1.5 text-sm font-medium text-primary/90 transition-colors hover:text-primary"
								>
									+{cat.articles.length - 4} more
								</a>
							</li>
						{/if}
					</ul>
				</section>
			{/each}
		</div>
	{/if}
</div>
