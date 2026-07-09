<script lang="ts">
	import { onMount } from 'svelte';
	import {
		ArrowRight,
		Play,
		Sparkles,
		Rocket,
		FlaskConical,
		FileSpreadsheet,
		ScanLine,
		Building2,
		FileSignature,
		CreditCard,
		Wallet,
		Wrench,
		Camera,
		Bot
	} from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import MarketingNav from '$lib/components/marketing/MarketingNav.svelte';
	import MarketingFooter from '$lib/components/marketing/MarketingFooter.svelte';
	import { startSmoothScroll } from '$lib/scroll/smooth';

	// Scroll-reveal action (shared `.reveal` CSS lives in app.css).
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

	onMount(() => {
		const stopSmooth = startSmoothScroll();
		let killParallax = () => {};
		(async () => {
			const reduce = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
			if (reduce) return;
			const { gsap } = await import('gsap');
			const { ScrollTrigger } = await import('gsap/ScrollTrigger');
			gsap.registerPlugin(ScrollTrigger);
			const tweens: { kill: () => void; scrollTrigger?: { kill: () => void } }[] = [];
			document.querySelectorAll<HTMLElement>('[data-parallax]').forEach((el) => {
				const range = Number(el.dataset.parallax) || 40;
				const t = gsap.fromTo(
					el,
					{ y: range },
					{
						y: -range,
						ease: 'none',
						scrollTrigger: { trigger: el, start: 'top bottom', end: 'bottom top', scrub: true }
					}
				);
				tweens.push(t as unknown as { kill: () => void; scrollTrigger?: { kill: () => void } });
			});
			killParallax = () => tweens.forEach((t) => {
				t.scrollTrigger?.kill();
				t.kill();
			});
			ScrollTrigger.refresh();
		})();
		return () => {
			killParallax();
			stopSmooth();
		};
	});

	// Capabilities grouped into cinematic "acts". Each act owns the viewport as you scroll:
	// big type + a real product shot (browser or phone) with depth, glow, and parallax.
	const acts = [
		{
			key: 'setup',
			eyebrow: 'Getting started',
			title: 'Scan a lease. Get a whole portfolio.',
			body: 'Tour a fully-loaded example portfolio first to learn the system — then start your own, no commitment. The guided setup is the easy part: photograph a lease and Rental Command builds the property, its units, the tenants, and the lease for you. Snap it page by page and it stitches the photos into one document first. Scan receipts and payments to fill the ledger, mix in a spreadsheet, or just key it in — whatever’s fastest.',
			accent: '#a36bff',
			shot: '/landing/app/setup.webp',
			features: [
				{ icon: FlaskConical, label: 'Explore with example data', desc: 'Kick the tires on a fully-loaded portfolio before you add a thing — then switch to your own in a tap.' },
				{ icon: ScanLine, label: 'A lease becomes everything', desc: 'Photograph a lease (even page by page — it stitches them) and it creates the property, units, tenants, and lease.' },
				{ icon: Rocket, label: 'Guided setup, your way', desc: 'Scan documents, import a spreadsheet, key it in, or any mix — a checklist walks you through it.' }
			]
		},
		{
			key: 'capture',
			eyebrow: 'The flagship',
			title: 'Scan it, say it, or type it.',
			body: 'Snap a receipt or bill, photograph a lease or application, drop in a PDF, or record a voice note. The AI reads every field and hands you a ready-to-confirm draft — the computer does the typing.',
			accent: '#22d3ee',
			shot: '/landing/app/scanadd.webp',
			features: [
				{ icon: ScanLine, label: 'Capture anything', desc: 'Receipts, bills, checks, leases, applications — photo, file, voice, or text.' },
				{ icon: Sparkles, label: 'AI extraction', desc: 'Every field pulled with a confidence score; you just confirm.' }
			]
		},
		{
			key: 'money',
			eyebrow: 'Money',
			title: 'Every dollar, reconciled.',
			body: 'Collect rent online with autopay, then watch real double-entry books reconcile against your bank feed. One paged ledger holds every payment, expense, deposit, and withdrawal — exportable for your accountant.',
			accent: '#34d399',
			shot: '/landing/app/money.webp',
			features: [
				{ icon: CreditCard, label: 'Rent & autopay', desc: 'Online rent with autopay, so you never chase a payment again.' },
				{ icon: Wallet, label: 'Real accounting & reports', desc: 'Double-entry books, reconciliation, P&L, Schedule-E, and year-end packets.' }
			]
		},
		{
			key: 'rentals',
			eyebrow: 'Rentals',
			title: 'Properties, units, and leases — organized.',
			body: 'Every building, unit, and tenant in one place, with occupancy at a glance. Generate a lease, send it for e-signature, take applications online, and screen applicants in a click.',
			accent: '#f59e0b',
			shot: '/landing/app/properties.webp',
			features: [
				{ icon: Building2, label: 'Properties & units', desc: 'Buildings, units, tenants, and occupancy — structured and searchable.' },
				{ icon: FileSignature, label: 'Leases & applications', desc: 'E-signature leases, online applications, one-click screening.' }
			]
		},
		{
			key: 'ops',
			eyebrow: 'Operations',
			title: 'From request to dispatched, in seconds.',
			body: 'Turn a tenant’s text into a tracked work order across any property — priority, vendor, photos, and compliance. Keep vendors and 1099s straight, and schedule showings, inspections, and move-ins on one calendar.',
			accent: '#60a5fa',
			shot: '/landing/app/workorders.webp',
			features: [
				{ icon: Wrench, label: 'Work orders & vendors', desc: 'Dispatch and track work; vendor ratings and 1099 compliance kept straight.' },
				{ icon: Camera, label: 'Inspections & scheduling', desc: 'Photo-per-item reports and a calendar for every showing and visit.' }
			]
		},
		{
			key: 'ai',
			eyebrow: 'Intelligence',
			title: 'A portfolio that briefs you.',
			body: 'Open the app to a daily briefing that reads the whole portfolio overnight and tells you what needs you first. Ask anything in plain English and get answers grounded in your own live data.',
			accent: '#c9acff',
			shot: '/landing/app/dashboard.webp',
			features: [
				{ icon: Bot, label: 'Ask your portfolio', desc: 'Plain-English answers drawn from your real numbers.' },
				{ icon: Sparkles, label: 'Daily briefing', desc: 'Overdue rent, expiring leases, and open repairs — surfaced each morning.' }
			]
		}
	];

	// A few more real screens — to make the point that this still isn't everything.
	const more = [
		{ shot: '/landing/app/leases-full.webp', label: 'Leases' },
		{ shot: '/landing/app/vendors.webp', label: 'Vendors & 1099s' },
		{ shot: '/landing/app/appointments.webp', label: 'Scheduling' },
		{ shot: '/landing/app/reports.webp', label: 'Reports' }
	];

	const pills = ['Setup', 'Capture', 'Money', 'Rentals', 'Operations', 'AI'];
</script>

<svelte:head>
	<title>Features — Rental Command</title>
	<meta
		name="description"
		content="Everything it takes to run a small portfolio: leases & e-signature, online rent & autopay, work orders, screening, real double-entry accounting, inspections, a tenant portal, and an AI you can ask about your own data."
	/>
</svelte:head>

<div class="relative bg-background text-foreground">
	<MarketingNav current="features" />

	<!-- ===================== HERO ===================== -->
	<section class="relative overflow-hidden">
		<div
			class="pointer-events-none absolute inset-0 -z-10"
			style="background-image: radial-gradient(45% 55% at 22% 10%, color-mix(in oklab, var(--primary) 24%, transparent), transparent 70%), radial-gradient(42% 52% at 84% 24%, color-mix(in oklab, var(--chart-4) 16%, transparent), transparent 70%);"
			aria-hidden="true"
		></div>
		<div class="dotgrid pointer-events-none absolute inset-0 -z-10" aria-hidden="true"></div>

		<div class="mx-auto max-w-4xl px-5 pb-10 pt-28 text-center sm:px-8 sm:pt-36" use:reveal>
			<span
				class="inline-flex items-center gap-2 rounded-full border border-primary/25 bg-primary/10 px-3 py-1 text-xs font-semibold text-primary"
			>
				<Sparkles class="h-3.5 w-3.5" />
				Features
			</span>
			<h1 class="display mx-auto mt-6 max-w-3xl text-5xl font-semibold leading-[1.03] tracking-tight sm:text-6xl">
				Everything it takes to <span class="grad">run the building.</span>
			</h1>
			<p class="mx-auto mt-6 max-w-2xl text-lg leading-relaxed text-muted-foreground">
				Document- and voice-capture on top of a real management system — leases, money, maintenance,
				and tenants, all in one calm place.
			</p>

			<div class="mt-9 flex flex-wrap items-center justify-center gap-3">
				<Button href="/register" size="lg" class="h-12 px-7 text-base">
					Get started free
					<ArrowRight class="h-4 w-4" />
				</Button>
				<Button href="/welcome" variant="outline" size="lg" class="h-12 px-6 text-base">
					<Play class="h-4 w-4" />
					See it in action
				</Button>
			</div>

			<ul class="mt-10 flex flex-wrap items-center justify-center gap-2.5">
				{#each pills as p (p)}
					<li
						class="rounded-full border border-border/70 bg-card/50 px-3.5 py-1.5 text-xs font-medium text-muted-foreground backdrop-blur"
					>
						{p}
					</li>
				{/each}
			</ul>
		</div>

		<!-- hero product shot -->
		<div class="mx-auto max-w-5xl px-5 pb-16 sm:px-8">
			<div class="hero-shot" data-parallax="26">
				<div class="window">
					<span class="win-glow" aria-hidden="true"></span>
					<div class="chrome" aria-hidden="true">
						<span class="dots"><i></i><i></i><i></i></span>
						<span class="url">rentalcommand.net<span class="path"> / units / Unit&nbsp;1</span></span>
					</div>
					<img src="/landing/app/commandcenter.webp" alt="The Rental Command web app" width="1600" height="888" loading="eager" decoding="async" />
				</div>
			</div>
		</div>
	</section>

	<!-- ===================== CINEMATIC ACTS ===================== -->
	{#each acts as act, i (act.key)}
		<section class="act {i % 2 === 1 ? 'reverse' : ''}" style={`--accent:${act.accent}`}>
			<span class="act-glow" aria-hidden="true"></span>
			<div class="act-inner">
				<div class="act-copy" use:reveal>
					<span class="act-eyebrow">{act.eyebrow}</span>
					<h2 class="act-title">{act.title}</h2>
					<p class="act-body">{act.body}</p>
					<ul class="act-feats">
						{#each act.features as f (f.label)}
							<li>
								<span class="feat-ic"><f.icon class="h-4 w-4" /></span>
								<span class="feat-txt"><strong>{f.label}.</strong> {f.desc}</span>
							</li>
						{/each}
					</ul>
				</div>

				<div class="act-visual" data-parallax="34">
					<div class="window window-lg">
						<span class="win-glow" aria-hidden="true"></span>
						<div class="chrome" aria-hidden="true">
							<span class="dots"><i></i><i></i><i></i></span>
							<span class="url">rentalcommand.net</span>
						</div>
						<img src={act.shot} alt={`${act.eyebrow} in Rental Command`} width="1600" height="888" loading="lazy" decoding="async" />
					</div>
				</div>
			</div>
		</section>
	{/each}

	<!-- ===================== AND THERE'S MORE ===================== -->
	<section class="border-t border-border/60">
		<div class="mx-auto max-w-6xl px-5 py-24 sm:px-8">
			<div class="mx-auto max-w-2xl text-center" use:reveal>
				<span class="text-sm font-semibold uppercase tracking-wide text-primary">More in the app</span>
				<h2 class="display mx-auto mt-3 text-4xl font-semibold tracking-tight sm:text-5xl">
					…and this still isn't everything.
				</h2>
				<p class="mt-4 text-lg text-muted-foreground">
					Leases, vendors and 1099s, scheduling, owner statements, reports — a few more of the screens
					you'll actually use.
				</p>
			</div>
			<div class="mx-auto mt-14 grid max-w-4xl gap-8">
				{#each more as m (m.label)}
					<figure class="more-card" use:reveal>
						<div class="more-win">
							<img
								src={m.shot}
								alt={`${m.label} — Rental Command`}
								width="1600"
								height="888"
								loading="lazy"
								decoding="async"
							/>
						</div>
						<figcaption class="more-cap">{m.label}</figcaption>
					</figure>
				{/each}
			</div>
		</div>
	</section>

	<!-- ===================== CLOSING CTA ===================== -->
	<section class="relative overflow-hidden border-t border-border/60">
		<div
			class="pointer-events-none absolute inset-0 -z-10"
			style="background-image: radial-gradient(55% 60% at 50% -5%, color-mix(in oklab, var(--primary) 16%, transparent), transparent 70%);"
			aria-hidden="true"
		></div>
		<div class="mx-auto max-w-4xl px-5 py-28 text-center sm:px-8" use:reveal>
			<h2 class="display mx-auto max-w-2xl text-4xl font-semibold tracking-tight sm:text-5xl">
				Stop typing. Start confirming.
			</h2>
			<p class="mx-auto mt-5 max-w-xl text-lg leading-relaxed text-muted-foreground">
				Create your account and let Rental Command turn your documents — and your voice — into records.
			</p>
			<div class="mt-9 flex flex-wrap items-center justify-center gap-3">
				<Button href="/register" size="lg" class="h-12 px-7 text-base">
					Get started free
					<ArrowRight class="h-4 w-4" />
				</Button>
				<Button href="/welcome" variant="outline" size="lg" class="h-12 px-6 text-base">
					Back to overview
				</Button>
			</div>
			<p class="mt-8 text-xs text-muted-foreground">No credit card required.</p>
		</div>
	</section>

	<MarketingFooter />
</div>

<style>
	:global(html),
	:global(body) {
		overflow: visible;
		height: auto;
	}

	.display {
		font-family: var(--m3-font-display);
		letter-spacing: -0.02em;
	}

	.grad {
		background: linear-gradient(115deg, var(--primary), var(--chart-4));
		-webkit-background-clip: text;
		background-clip: text;
		color: transparent;
	}

	.dotgrid {
		background-image: radial-gradient(circle, rgba(255, 255, 255, 0.05) 1px, transparent 1px);
		background-size: 24px 24px;
		-webkit-mask-image: radial-gradient(70% 60% at 50% 22%, #000, transparent 75%);
		mask-image: radial-gradient(70% 60% at 50% 22%, #000, transparent 75%);
		opacity: 0.6;
	}

	/* ---- shared browser window frame ---- */
	.window {
		position: relative;
		border-radius: 0.85rem;
		overflow: hidden;
		background: #0c0c12;
		border: 1px solid rgba(255, 255, 255, 0.12);
		box-shadow:
			0 50px 100px -45px rgba(0, 0, 0, 0.9),
			inset 0 1px 0 rgba(255, 255, 255, 0.06);
	}
	.win-glow {
		position: absolute;
		inset: -25%;
		z-index: 0;
		pointer-events: none;
		background: radial-gradient(circle at 60% 0%, color-mix(in oklab, var(--accent, #7c5cff) 34%, transparent), transparent 60%);
		filter: blur(34px);
	}
	.chrome {
		position: relative;
		z-index: 1;
		display: flex;
		align-items: center;
		gap: 0.7rem;
		padding: 0.55rem 0.8rem;
		background: rgba(255, 255, 255, 0.035);
		border-bottom: 1px solid rgba(255, 255, 255, 0.06);
	}
	.dots {
		display: inline-flex;
		gap: 0.4rem;
	}
	.dots i {
		width: 0.58rem;
		height: 0.58rem;
		border-radius: 50%;
		background: #3a3a46;
	}
	.dots i:nth-child(1) { background: #ff5f57; opacity: 0.7; }
	.dots i:nth-child(2) { background: #febc2e; opacity: 0.7; }
	.dots i:nth-child(3) { background: #28c840; opacity: 0.7; }
	.url {
		font-size: 0.72rem;
		color: #8f8da4;
		background: rgba(255, 255, 255, 0.05);
		border-radius: 999px;
		padding: 0.2rem 0.75rem;
	}
	.url .path { color: #6f6e84; }
	.window img {
		position: relative;
		z-index: 1;
		display: block;
		width: 100%;
		aspect-ratio: 1600 / 888;
		object-fit: cover;
		object-position: top left;
	}

	.hero-shot {
		position: relative;
		transform: perspective(1800px) rotateX(6deg);
		transform-style: preserve-3d;
	}
	.hero-shot .window {
		max-width: 64rem;
		margin: 0 auto;
	}

	/* ---- cinematic acts ---- */
	.act {
		position: relative;
		overflow: hidden;
		border-top: 1px solid rgba(255, 255, 255, 0.05);
	}
	.act-glow {
		position: absolute;
		z-index: 0;
		top: 50%;
		left: 12%;
		width: 42vw;
		height: 42vw;
		max-width: 640px;
		max-height: 640px;
		transform: translateY(-50%);
		border-radius: 50%;
		pointer-events: none;
		background: radial-gradient(circle, color-mix(in oklab, var(--accent) 16%, transparent), transparent 65%);
		filter: blur(70px);
	}
	.act.reverse .act-glow {
		left: auto;
		right: 12%;
	}
	.act-inner {
		position: relative;
		z-index: 1;
		display: grid;
		grid-template-columns: minmax(0, 0.64fr) minmax(0, 1.36fr);
		align-items: center;
		gap: clamp(1.75rem, 4vw, 4rem);
		max-width: 84rem;
		margin: 0 auto;
		min-height: 86svh;
		padding: clamp(4rem, 9vh, 7rem) clamp(1.5rem, 5vw, 4rem);
	}
	/* keep the SCREENSHOT in the wide column on both sides */
	.act.reverse .act-inner {
		grid-template-columns: minmax(0, 1.36fr) minmax(0, 0.64fr);
	}
	.act.reverse .act-visual {
		order: -1;
	}

	/* hover to lean the screenshot in (desktop) */
	.act-visual .window {
		transition: transform 0.45s cubic-bezier(0.22, 1, 0.36, 1), box-shadow 0.45s;
	}
	.act-visual:hover .window {
		transform: scale(1.04);
	}

	.act-eyebrow {
		display: inline-block;
		font-size: 0.8rem;
		font-weight: 700;
		letter-spacing: 0.06em;
		text-transform: uppercase;
		color: var(--accent);
		margin-bottom: 1rem;
	}
	.act-title {
		margin: 0;
		font-family: var(--m3-font-display, 'Funnel Display', system-ui), sans-serif;
		font-weight: 600;
		font-size: clamp(2.1rem, 4.2vw, 3.4rem);
		line-height: 1.04;
		letter-spacing: -0.025em;
		color: #f6f5fb;
		text-shadow: 0 2px 40px rgba(0, 0, 0, 0.5);
	}
	.act-body {
		margin: 1.1rem 0 0;
		max-width: 32rem;
		font-size: clamp(1rem, 1.3vw, 1.15rem);
		line-height: 1.6;
		color: #b6b3c6;
	}
	.act-feats {
		list-style: none;
		margin: 1.8rem 0 0;
		padding: 0;
		display: flex;
		flex-direction: column;
		gap: 1rem;
	}
	.act-feats li {
		display: flex;
		gap: 0.85rem;
		align-items: flex-start;
	}
	.feat-ic {
		display: inline-flex;
		align-items: center;
		justify-content: center;
		width: 2.1rem;
		height: 2.1rem;
		flex: none;
		border-radius: 0.65rem;
		color: var(--accent);
		background: color-mix(in oklab, var(--accent) 14%, transparent);
		border: 1px solid color-mix(in oklab, var(--accent) 32%, transparent);
	}
	.feat-txt {
		font-size: 0.95rem;
		line-height: 1.5;
		color: #a7a5ba;
	}
	.feat-txt strong {
		color: #f1f0f7;
		font-weight: 600;
	}

	.act-visual {
		min-width: 0;
		will-change: transform;
	}

	/* ---- "and there's more" gallery ---- */
	.more-card {
		margin: 0;
	}
	.more-win {
		border-radius: 0.7rem;
		overflow: hidden;
		border: 1px solid rgba(255, 255, 255, 0.1);
		background: #0c0c12;
		box-shadow: 0 24px 50px -30px rgba(0, 0, 0, 0.85);
		transition: transform 0.3s ease, border-color 0.3s ease;
	}
	.more-card:hover .more-win {
		transform: translateY(-4px);
		border-color: rgba(164, 107, 255, 0.4);
	}
	.more-win img {
		display: block;
		width: 100%;
		aspect-ratio: 1600 / 888;
		object-fit: cover;
		object-position: top center;
	}
	.more-cap {
		margin-top: 0.7rem;
		text-align: center;
		font-size: 0.9rem;
		font-weight: 600;
		color: #c9c6d6;
	}

	@media (max-width: 900px) {
		.act-inner,
		.act.reverse .act-inner {
			grid-template-columns: 1fr;
			min-height: 0;
			gap: 2.5rem;
			padding-top: clamp(3.5rem, 8vh, 5rem);
			padding-bottom: clamp(3.5rem, 8vh, 5rem);
		}
		/* stack copy → screenshot (same as the normal acts) */
		.act.reverse .act-visual {
			order: 0;
		}
		.act-glow {
			left: 50%;
			right: auto;
			transform: translate(-50%, -50%);
		}
		.hero-shot {
			transform: none;
		}
	}

	@media (prefers-reduced-motion: reduce) {
		.hero-shot {
			transform: none;
		}
		.act-visual {
			transform: none !important;
		}
	}
</style>
