<script lang="ts">
	import { ArrowLeft, ArrowRight, BookOpen, Menu, X } from '@lucide/svelte';
	import type { PageData } from './$types';

	let { data }: { data: PageData } = $props();

	const article = $derived(data.article);
	const categories = $derived(data.index.categories ?? []);
	let mobileNavOpen = $state(false);
</script>

<svelte:head>
	<title>{article.title} — Rental Command Docs</title>
	<meta name="description" content={article.summary || `${article.title} — Rental Command documentation.`} />
</svelte:head>

<div class="mx-auto max-w-6xl px-5 py-10 sm:px-8 lg:py-14">
	<!-- Breadcrumb / back -->
	<a
		href="/docs"
		class="inline-flex items-center gap-1.5 text-sm font-medium text-muted-foreground transition-colors hover:text-foreground"
		data-testid="docs-back"
	>
		<ArrowLeft class="h-4 w-4" />
		All docs
	</a>

	<div class="mt-6 grid gap-8 lg:grid-cols-[16rem_1fr]">
		<!-- Sidebar: other articles -->
		<aside class="lg:sticky lg:top-24 lg:self-start">
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
				{#each categories as cat (cat.category)}
					<div>
						<p class="mb-1.5 px-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground/70">
							{cat.category}
						</p>
						<ul class="space-y-0.5">
							{#each cat.articles as item (item.slug)}
								{@const active = item.slug === article.slug}
								<a
									href="/docs/{item.slug}"
									aria-current={active ? 'page' : undefined}
									class="block rounded-md px-2 py-1.5 text-sm transition-colors {active
										? 'bg-primary/10 font-medium text-primary'
										: 'text-muted-foreground hover:bg-secondary hover:text-foreground'}"
									data-testid="docs-sidebar-link-{item.slug}"
								>
									{item.title}
								</a>
							{/each}
						</ul>
					</div>
				{/each}
			</nav>
		</aside>

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
