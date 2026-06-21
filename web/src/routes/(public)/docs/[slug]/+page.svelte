<script lang="ts">
	import { ArrowLeft, ArrowRight, BookOpen, ChevronRight, List } from '@lucide/svelte';
	import type { PageData } from './$types';

	let { data }: { data: PageData } = $props();

	const article = $derived(data.article);
	const categories = $derived(data.index.categories ?? []);

	// "On this page" table of contents (h2/h3 anchors), built server-side.
	const toc = $derived(data.toc ?? []);
	const showToc = $derived(toc.length >= 2);

	// The category bucket this article belongs to — for the breadcrumb's category link.
	const currentCategory = $derived(
		categories.find((c) => c.articles.some((a) => a.slug === article.slug)) ?? null
	);
	const categoryHref = $derived(
		currentCategory?.articles[0] ? `/docs/${currentCategory.articles[0].slug}` : '/docs'
	);

	// Scroll-spy: highlight the section currently at the top of the reader. The docs
	// scroll inside the marketing shell's own scroll container (html/body are
	// overflow:hidden), so an IntersectionObserver against the viewport misfired —
	// track the actual scroll container and pick the last heading scrolled past.
	let activeId = $state('');
	$effect(() => {
		const ids = toc.map((t) => t.id); // re-runs when navigating to another article
		if (ids.length === 0) return;
		const scroller: HTMLElement | Window =
			(document.querySelector('.marketing-shell') as HTMLElement | null) ?? window;

		const update = () => {
			const threshold = 120; // a touch below the 64px sticky nav
			let current = ids[0];
			for (const id of ids) {
				const el = document.getElementById(id);
				if (!el) continue;
				if (el.getBoundingClientRect().top - threshold <= 0) current = id;
				else break;
			}
			activeId = current;
		};

		let raf = 0;
		const onScroll = () => {
			if (!raf) raf = requestAnimationFrame(() => { raf = 0; update(); });
		};
		scroller.addEventListener('scroll', onScroll, { passive: true });
		update();
		return () => {
			scroller.removeEventListener('scroll', onScroll);
			if (raf) cancelAnimationFrame(raf);
		};
	});
</script>

<svelte:head>
	<title>{article.title} — Rental Command Docs</title>
	<meta name="description" content={article.summary || `${article.title} — Rental Command documentation.`} />
</svelte:head>

<div class="mx-auto max-w-6xl px-5 py-10 sm:px-8 lg:py-14">
	<!-- Breadcrumb: Docs / Category / Article -->
	<nav class="flex items-center gap-1.5 text-sm text-muted-foreground" data-testid="docs-breadcrumb">
		<a href="/docs" class="transition-colors hover:text-foreground" data-testid="docs-back">Docs</a>
		{#if article.category}
			<ChevronRight class="h-3.5 w-3.5 text-muted-foreground/40" />
			<a href={categoryHref} class="transition-colors hover:text-foreground">{article.category}</a>
		{/if}
		<ChevronRight class="h-3.5 w-3.5 text-muted-foreground/40" />
		<span class="truncate font-medium text-foreground">{article.title}</span>
	</nav>

	<div class="mt-6 grid gap-10 {showToc ? 'lg:grid-cols-[1fr_14rem]' : ''}">
		<!-- Article -->
		<article class="min-w-0" data-testid="docs-article" data-doc-slug={article.slug}>
			<div class="mb-2 flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-primary">
				<BookOpen class="h-3.5 w-3.5" />
				{article.category}
			</div>
			<h1 class="text-3xl font-semibold tracking-tight sm:text-4xl">{article.title}</h1>
			{#if article.summary}
				<p class="mt-3 text-lg leading-relaxed text-muted-foreground">{article.summary}</p>
			{/if}

			<hr class="my-7 border-border/60" />

			<!-- Rendered markdown (trusted, server-rendered + scrubbed). -->
			<div class="doc-prose max-w-none" data-testid="docs-article-{article.slug}">
				<!-- eslint-disable-next-line svelte/no-at-html-tags -->
				{@html data.bodyHtml}
			</div>

			<!-- Prev / next -->
			<nav class="mt-12 grid gap-4 border-t border-border/60 pt-6 sm:grid-cols-2" data-testid="docs-prev-next">
				{#if data.prev}
					<a
						href="/docs/{data.prev.slug}"
						class="group flex flex-col rounded-xl border border-border bg-card p-4 transition-colors hover:border-primary/40"
						data-testid="docs-prev"
					>
						<span class="inline-flex items-center gap-1 text-xs text-muted-foreground">
							<ArrowLeft class="h-3.5 w-3.5" /> Previous
						</span>
						<span class="mt-1 text-sm font-medium transition-colors group-hover:text-primary">{data.prev.title}</span>
					</a>
				{:else}
					<span></span>
				{/if}
				{#if data.next}
					<a
						href="/docs/{data.next.slug}"
						class="group flex flex-col items-end rounded-xl border border-border bg-card p-4 text-right transition-colors hover:border-primary/40"
						data-testid="docs-next"
					>
						<span class="inline-flex items-center gap-1 text-xs text-muted-foreground">
							Next <ArrowRight class="h-3.5 w-3.5" />
						</span>
						<span class="mt-1 text-sm font-medium transition-colors group-hover:text-primary">{data.next.title}</span>
					</a>
				{/if}
			</nav>
		</article>

		<!-- On this page: anchors to the article's own headings (right rail, desktop only). -->
		{#if showToc}
			<aside class="hidden lg:block">
				<div class="sticky top-24">
					<p class="mb-3 flex items-center gap-2 px-3 text-xs font-semibold uppercase tracking-wide text-muted-foreground/70">
						<List class="h-3.5 w-3.5" /> On this page
					</p>
					<nav class="space-y-0.5 border-l border-border/60" data-testid="docs-on-this-page" aria-label="On this page">
						{#each toc as item (item.id)}
							<a
								href="#{item.id}"
								class={`-ml-px block border-l-2 py-1 text-sm transition-colors ${item.level === 3 ? 'pl-6' : 'pl-3'} ${activeId === item.id ? 'border-primary font-medium text-primary' : 'border-transparent text-muted-foreground hover:border-border hover:text-foreground'}`}
								data-testid="docs-toc-{item.id}"
							>
								{item.text}
							</a>
						{/each}
					</nav>
				</div>
			</aside>
		{/if}
	</div>
</div>

<style>
	/* Markdown typography (no @tailwindcss/typography plugin in this project).
	   Styled to match the app's dark theme + tokens. Scoped to .doc-prose via
	   :global so it applies to the {@html} output. */
	.doc-prose :global(h1),
	.doc-prose :global(h2),
	.doc-prose :global(h3),
	.doc-prose :global(h4) {
		font-weight: 600;
		letter-spacing: -0.01em;
		color: var(--foreground);
		scroll-margin-top: 6rem;
	}
	.doc-prose :global(h2) {
		font-size: 1.5rem;
		line-height: 1.3;
		margin-top: 2rem;
		margin-bottom: 0.75rem;
		padding-bottom: 0.35rem;
		border-bottom: 1px solid color-mix(in oklab, var(--border) 80%, transparent);
	}
	.doc-prose :global(h3) {
		font-size: 1.2rem;
		line-height: 1.35;
		margin-top: 1.6rem;
		margin-bottom: 0.5rem;
	}
	.doc-prose :global(h4) {
		font-size: 1.05rem;
		margin-top: 1.3rem;
		margin-bottom: 0.4rem;
	}
	.doc-prose :global(p) {
		color: var(--muted-foreground);
		line-height: 1.75;
		margin: 0.9rem 0;
	}
	.doc-prose :global(a) {
		color: var(--primary);
		text-decoration: underline;
		text-underline-offset: 2px;
	}
	.doc-prose :global(a:hover) {
		opacity: 0.85;
	}
	.doc-prose :global(strong) {
		color: var(--foreground);
		font-weight: 600;
	}
	.doc-prose :global(ul),
	.doc-prose :global(ol) {
		color: var(--muted-foreground);
		margin: 0.9rem 0;
		padding-left: 1.4rem;
		line-height: 1.7;
	}
	.doc-prose :global(ul) {
		list-style: disc;
	}
	.doc-prose :global(ol) {
		list-style: decimal;
	}
	.doc-prose :global(li) {
		margin: 0.35rem 0;
	}
	.doc-prose :global(li > ul),
	.doc-prose :global(li > ol) {
		margin: 0.35rem 0;
	}
	.doc-prose :global(blockquote) {
		border-left: 3px solid var(--primary);
		background: color-mix(in oklab, var(--primary) 7%, transparent);
		margin: 1.2rem 0;
		padding: 0.6rem 1rem;
		border-radius: 0.25rem;
		color: var(--muted-foreground);
	}
	.doc-prose :global(blockquote p) {
		margin: 0.25rem 0;
	}
	.doc-prose :global(code) {
		font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
		font-size: 0.85em;
		background: var(--secondary);
		color: var(--foreground);
		padding: 0.15rem 0.4rem;
		border-radius: 0.35rem;
		border: 1px solid var(--border);
	}
	.doc-prose :global(pre) {
		background: var(--card);
		border: 1px solid var(--border);
		border-radius: 0.6rem;
		padding: 1rem 1.1rem;
		overflow-x: auto;
		margin: 1.2rem 0;
		font-size: 0.875rem;
		line-height: 1.6;
	}
	.doc-prose :global(pre code) {
		background: transparent;
		border: 0;
		padding: 0;
		font-size: inherit;
		color: var(--foreground);
	}
	.doc-prose :global(hr) {
		border: 0;
		border-top: 1px solid var(--border);
		margin: 1.75rem 0;
	}
	.doc-prose :global(img) {
		max-width: 100%;
		border-radius: 0.6rem;
		border: 1px solid var(--border);
		margin: 1.2rem 0;
	}
	.doc-prose :global(table) {
		width: 100%;
		border-collapse: collapse;
		margin: 1.2rem 0;
		font-size: 0.9rem;
		display: block;
		overflow-x: auto;
	}
	.doc-prose :global(th),
	.doc-prose :global(td) {
		border: 1px solid var(--border);
		padding: 0.55rem 0.75rem;
		text-align: left;
		vertical-align: top;
	}
	.doc-prose :global(th) {
		background: var(--secondary);
		color: var(--foreground);
		font-weight: 600;
	}
	.doc-prose :global(td) {
		color: var(--muted-foreground);
	}
	.doc-prose :global(tr:nth-child(even) td) {
		background: color-mix(in oklab, var(--muted) 35%, transparent);
	}
</style>
