<script lang="ts">
	import { onMount } from 'svelte';
	import { ArrowRight, Sparkles, Smartphone, Gauge, ShieldCheck } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import MarketingNav from '$lib/components/marketing/MarketingNav.svelte';
	import MarketingFooter from '$lib/components/marketing/MarketingFooter.svelte';
	import HouseHero from '$lib/components/marketing/HouseHero.svelte';
	import ScanCinematic from '$lib/components/marketing/ScanCinematic.svelte';
	import ScrubTabs from '$lib/components/marketing/ScrubTabs.svelte';
	import MobileShowcase from '$lib/components/marketing/MobileShowcase.svelte';
	import { startSmoothScroll, scrollTo } from '$lib/scroll/smooth';

	// --- Scroll-reveal action --------------------------------------------------
	// Adds `.is-visible` when the element scrolls into view. The `.reveal` /
	// `.reveal.is-visible` CSS (and its prefers-reduced-motion short-circuit) lives
	// globally in app.css, so this stays a one-liner per element.
	function reveal(node: HTMLElement, delay = 0) {
		node.classList.add('reveal');
		if (delay) node.style.transitionDelay = `${delay}ms`;

		const reduce =
			typeof window !== 'undefined' &&
			window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
		if (reduce) {
			node.classList.add('is-visible');
			return {};
		}

		const observer = new IntersectionObserver(
			(entries) => {
				for (const entry of entries) {
					if (entry.isIntersecting) {
						node.classList.add('is-visible');
						observer.unobserve(node);
					}
				}
			},
			{ threshold: 0.15, rootMargin: '0px 0px -10% 0px' }
		);
		observer.observe(node);
		return {
			destroy() {
				observer.disconnect();
			}
		};
	}

	// The page owns the smooth-scroll engine (Lenis + GSAP). MarketingNav and the
	// cinematic sections subscribe to it for their own state.
	let pageEl: HTMLDivElement;

	onMount(() => {
		const stopSmooth = startSmoothScroll();
		return () => stopSmooth();
	});

	function scrollToId(id: string) {
		scrollTo(`#${id}`);
	}

	// Static trust chips for the closing section (no animation — keeps memory low).
	const trust = [
		{ icon: Smartphone, label: 'Web + mobile, one login' },
		{ icon: Sparkles, label: 'The computer does the typing' },
		{ icon: Gauge, label: 'Fast at any scale' },
		{ icon: ShieldCheck, label: 'Your data stays yours' }
	];
</script>

<svelte:head>
	<title>Rental Command — the computer does the typing for you</title>
	<meta
		name="description"
		content="Scan a lease, invoice, or receipt — or just say it — and let AI extract the details. Rental Command turns documents and voice into ready-to-confirm records, so small landlords can run everything from their phone or their desk."
	/>
</svelte:head>

<div bind:this={pageEl} class="relative bg-background text-foreground">
	<MarketingNav />

	<!-- ===================== ① HERO (cinematic house scrub) ===================== -->
	<HouseHero onSeeItWork={() => scrollToId('scan')} />

	<!-- ===================== ② SCAN-TO-COMMAND CINEMATIC ===================== -->
	<ScanCinematic />

	<!-- ===================== ③ REAL COMMAND-CENTER TABS ===================== -->
	<ScrubTabs />

	<!-- ===================== ④ MOBILE — your whole portfolio, in your pocket ===================== -->
	<MobileShowcase />

	<!-- ===================== CLOSING (single light section) ===================== -->
	<section class="relative overflow-hidden border-t border-border/60">
		<!-- static radial glow (single paint, no animation) -->
		<div
			class="pointer-events-none absolute inset-0 -z-10"
			style="background-image: radial-gradient(55% 60% at 50% -5%, color-mix(in oklab, var(--primary) 16%, transparent), transparent 70%);"
			aria-hidden="true"
		></div>

		<div class="mx-auto max-w-4xl px-5 py-28 text-center sm:px-8" use:reveal>
			<span
				class="inline-flex items-center gap-2 rounded-full border border-primary/25 bg-primary/10 px-3 py-1 text-xs font-semibold text-primary"
			>
				<Sparkles class="h-3.5 w-3.5" />
				Getting started is easy
			</span>
			<h2 class="display mx-auto mt-6 max-w-2xl text-4xl font-semibold tracking-tight sm:text-5xl">
				Run every property from one calm place.
			</h2>
			<p class="mx-auto mt-5 max-w-2xl text-lg leading-relaxed text-muted-foreground">
				Explore a fully-loaded example portfolio first, or just scan your lease — Rental Command builds your properties, units, tenants, and ledger for you. The computer does the typing; you confirm.
			</p>

			<div class="mt-9 flex flex-wrap items-center justify-center gap-3">
				<Button href="/register" size="lg" class="h-12 px-7 text-base">
					Get started free
					<ArrowRight class="h-4 w-4" />
				</Button>
				<Button href="/features" variant="outline" size="lg" class="h-12 px-6 text-base">
					See all features
				</Button>
			</div>

			<ul class="mt-12 flex flex-wrap items-center justify-center gap-x-8 gap-y-3 text-sm text-muted-foreground">
				{#each trust as t (t.label)}
					<li class="inline-flex items-center gap-2">
						<t.icon class="h-4 w-4 text-primary" />
						{t.label}
					</li>
				{/each}
			</ul>

			<p class="mt-8 text-xs text-muted-foreground">No credit card required.</p>
		</div>
	</section>

	<MarketingFooter />
</div>

<style>
	/* The marketing landing scrolls the WINDOW (Lenis smooth scroll). Override the
	   app shell's global `html,body { overflow:hidden; height:100% }` while this
	   page is mounted so the document grows and scrolls naturally. */
	:global(html),
	:global(body) {
		overflow: visible;
		height: auto;
	}

	/* Display typeface for headlines (Funnel Display, via the M3 token). */
	.display {
		font-family: var(--m3-font-display);
		letter-spacing: -0.02em;
	}
</style>
