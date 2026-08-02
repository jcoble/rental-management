<script lang="ts">
	import { ArrowLeft, ArrowRight, CalendarDays, Clock3 } from '@lucide/svelte';
	import MarketingFooter from '$lib/components/marketing/MarketingFooter.svelte';
	import MarketingNav from '$lib/components/marketing/MarketingNav.svelte';
	import type { PageData } from './$types';

	let { data }: { data: PageData } = $props();
	const post = $derived(data.post);

	const publishedDate = $derived(
		new Date(`${post.publishedOn}T12:00:00Z`).toLocaleDateString('en-US', {
			month: 'long',
			day: 'numeric',
			year: 'numeric',
			timeZone: 'UTC'
		})
	);
</script>

<svelte:head>
	<title>{post.seoTitle}</title>
	<meta name="description" content={post.description} />
	<meta property="og:type" content="article" />
	<meta property="og:title" content={post.title} />
	<meta property="og:description" content={post.description} />
	<meta property="article:published_time" content={post.publishedOn} />
</svelte:head>

<div class="blog-shell h-dvh overflow-y-auto bg-background text-foreground">
	<MarketingNav current="blog" />

	<main>
		<article>
			<header class="border-b border-border/60">
				<div class="mx-auto max-w-4xl px-5 py-10 sm:px-8 sm:py-16">
					<a href="/blog" class="inline-flex items-center gap-2 text-sm font-medium text-muted-foreground transition-colors hover:text-foreground">
						<ArrowLeft class="size-4" /> All guides
					</a>
					<h1 class="mt-7 max-w-3xl text-4xl font-semibold tracking-[-0.035em] sm:text-6xl">{post.title}</h1>
					<p class="mt-6 max-w-3xl text-lg leading-8 text-muted-foreground">{post.summary}</p>
					<div class="mt-7 flex flex-wrap gap-x-5 gap-y-2 text-sm text-muted-foreground">
						<span class="inline-flex items-center gap-1.5"><CalendarDays class="size-4" /> {publishedDate}</span>
						<span class="inline-flex items-center gap-1.5"><Clock3 class="size-4" /> {post.readingTime}</span>
					</div>
				</div>
			</header>

			<div class="mx-auto grid max-w-4xl gap-10 px-5 py-10 sm:px-8 sm:py-14 lg:grid-cols-[9rem_1fr]">
				<aside class="hidden lg:block" aria-label="Article record">
					<div class="sticky top-24 border-l border-primary/40 pl-4 text-xs text-muted-foreground">
						<p class="font-mono font-semibold text-foreground">BOOK ENTRY</p>
						<p class="mt-2">{publishedDate}</p>
						<p class="mt-1">{post.readingTime}</p>
					</div>
				</aside>

				<div class="article-copy min-w-0">
					{#each post.intro as paragraph}
						<p class="intro">{paragraph}</p>
					{/each}

					{#each post.sections as section}
						<section>
							<h2>{section.heading}</h2>
							{#each section.paragraphs as paragraph}<p>{paragraph}</p>{/each}
							{#if section.bullets}
								<ul>{#each section.bullets as bullet}<li>{bullet}</li>{/each}</ul>
							{/if}
						</section>
					{/each}

					<section class="cta mt-12 rounded-2xl border border-primary/30 bg-primary/10 p-6 sm:p-8">
						<h2 class="mt-0">{post.cta.heading}</h2>
						<p>{post.cta.body}</p>
						<a href={post.cta.href} class="mt-5 inline-flex items-center gap-2 rounded-full bg-primary px-5 py-2.5 text-sm font-semibold text-primary-foreground transition-transform hover:translate-x-0.5 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2">
							{post.cta.label} <ArrowRight class="size-4" />
						</a>
					</section>
				</div>
			</div>
		</article>
	</main>

	<MarketingFooter />
</div>

<style>
	.article-copy :global(p) {
		margin: 1rem 0;
		color: var(--muted-foreground);
		font-size: 1.0625rem;
		line-height: 1.8;
	}
	.article-copy :global(p.intro) {
		color: var(--foreground);
		font-size: 1.15rem;
		line-height: 1.75;
	}
	.article-copy :global(h2) {
		margin-top: 2.5rem;
		margin-bottom: 0.75rem;
		font-size: 1.5rem;
		font-weight: 650;
		letter-spacing: -0.02em;
	}
	.article-copy :global(ul) {
		margin: 1rem 0;
		padding-left: 1.25rem;
		color: var(--muted-foreground);
		line-height: 1.75;
		list-style: disc;
	}
	.article-copy :global(li) {
		margin: 0.4rem 0;
		padding-left: 0.25rem;
	}
	.article-copy .cta :global(p) {
		max-width: 38rem;
	}
	@media (prefers-reduced-motion: reduce) {
		.article-copy :global(a) {
			transition: none;
		}
	}
</style>
