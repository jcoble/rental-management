<script lang="ts">
	import { ArrowRight, BookOpen, CalendarDays } from '@lucide/svelte';
	import MarketingFooter from '$lib/components/marketing/MarketingFooter.svelte';
	import MarketingNav from '$lib/components/marketing/MarketingNav.svelte';
	import { BLOG_POSTS } from '$lib/blog/articles';

	function publishedDate(value: string): string {
		return new Date(`${value}T12:00:00Z`).toLocaleDateString('en-US', {
			month: 'long',
			day: 'numeric',
			year: 'numeric',
			timeZone: 'UTC'
		});
	}
</script>

<svelte:head>
	<title>Rental Bookkeeping Guides for Landlords | Rental Command</title>
	<meta
		name="description"
		content="Plain-English rental bookkeeping guides for landlords managing payments, lease ledgers, security deposits, cash flow, and audit records."
	/>
</svelte:head>

<div class="blog-shell h-dvh overflow-y-auto bg-background text-foreground">
	<MarketingNav current="blog" />

	<main>
		<section class="border-b border-border/60">
			<div class="mx-auto max-w-6xl px-5 py-16 sm:px-8 sm:py-24">
				<div class="max-w-3xl">
					<p class="flex items-center gap-2 text-sm font-semibold text-primary">
						<BookOpen class="size-4" />
						Rental bookkeeping, explained
					</p>
					<h1 class="mt-5 text-4xl font-semibold tracking-[-0.035em] sm:text-6xl">
						Better books start with a record you can explain.
					</h1>
					<p class="mt-6 max-w-2xl text-lg leading-8 text-muted-foreground">
						Practical guidance for landlords moving from spreadsheets to organized rental records. No accounting degree required.
					</p>
				</div>
			</div>
		</section>

		<section class="mx-auto max-w-6xl px-5 py-12 sm:px-8 sm:py-16" aria-labelledby="latest-guides">
			<div class="mb-7 flex items-end justify-between gap-4">
				<div>
					<p class="text-xs font-semibold uppercase tracking-[0.16em] text-muted-foreground">The landlord's books</p>
					<h2 id="latest-guides" class="mt-2 text-2xl font-semibold tracking-tight">Latest guides</h2>
				</div>
				<span class="hidden text-sm text-muted-foreground sm:block">Built for 15–40 unit portfolios</span>
			</div>

			<div class="record-list border-y border-border/70">
				{#each BLOG_POSTS as post, index (post.slug)}
					<article class="record-row group grid gap-5 border-b border-border/70 py-7 last:border-b-0 sm:grid-cols-[7.5rem_1fr_auto] sm:items-start sm:py-9">
						<div class="text-sm text-muted-foreground">
							<p class="font-mono text-xs tabular-nums">ENTRY {String(index + 1).padStart(2, '0')}</p>
							<p class="mt-2 flex items-center gap-1.5"><CalendarDays class="size-3.5" /> {publishedDate(post.publishedOn)}</p>
						</div>
						<div class="min-w-0">
							<h3 class="text-xl font-semibold tracking-tight transition-colors group-hover:text-primary sm:text-2xl">
								<a href="/blog/{post.slug}" class="focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-4">
									{post.title}
								</a>
							</h3>
							<p class="mt-3 max-w-3xl leading-7 text-muted-foreground">{post.summary}</p>
							<p class="mt-4 text-xs font-medium uppercase tracking-wide text-muted-foreground">{post.readingTime}</p>
						</div>
						<a
							href="/blog/{post.slug}"
							class="inline-flex size-10 items-center justify-center rounded-full border border-border transition-colors hover:border-primary hover:bg-primary/10 hover:text-primary"
							aria-label="Read {post.title}"
						>
							<ArrowRight class="size-4" />
						</a>
					</article>
				{/each}
			</div>
		</section>
	</main>

	<MarketingFooter />
</div>

<style>
	.record-list {
		background-image: linear-gradient(to right, color-mix(in srgb, var(--primary) 24%, transparent) 1px, transparent 1px);
		background-position: 8.5rem 0;
		background-repeat: repeat-y;
	}
	@media (max-width: 639px) {
		.record-list {
			background-image: none;
		}
	}
</style>
