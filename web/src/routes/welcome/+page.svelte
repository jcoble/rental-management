<script lang="ts">
	import {
		Building,
		ScanLine,
		Sparkles,
		FileCheck2,
		ArrowRight,
		Camera,
		Mic,
		FileText,
		Wallet,
		Wrench,
		MessageSquare,
		BarChart3,
		ShieldCheck,
		Bot,
		Check,
		Receipt,
		Zap,
		Wifi,
		BatteryFull,
		Signal,
		ChevronRight
	} from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';

	// --- Scroll-reveal action --------------------------------------------------
	// Adds `.is-visible` when the element scrolls into view. Honors
	// prefers-reduced-motion (the CSS short-circuits the transition there).
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

	const steps = [
		{
			icon: Camera,
			title: 'Capture',
			body: 'Snap a photo, upload a PDF, or just talk. A lease, an invoice, a rent receipt — anything.'
		},
		{
			icon: Sparkles,
			title: 'Extract',
			body: 'The AI reads the document and pulls out the fields, each with a confidence score.'
		},
		{
			icon: FileCheck2,
			title: 'Confirm',
			body: 'Review a ready-made draft, fix anything that looks off, and confirm. The record is created.'
		}
	];

	const features = [
		{ icon: FileText, title: 'Leases & tenants', body: 'Track terms, renewals, and signatures in one place.', tint: 'text-chart-1' },
		{ icon: Wallet, title: 'Money, sorted', body: 'Rent, expenses, deposits, and a plain-English snapshot.', tint: 'text-chart-2' },
		{ icon: Wrench, title: 'Maintenance', body: 'Work orders, vendors, and appointments that stay on schedule.', tint: 'text-chart-3' },
		{ icon: MessageSquare, title: 'Tenant messaging', body: 'Portal, email, and SMS threads — never lose a conversation.', tint: 'text-chart-4' },
		{ icon: BarChart3, title: 'Insights', body: 'Occupancy, cash flow, and what needs your attention today.', tint: 'text-chart-1' },
		{ icon: Bot, title: 'Ask your portfolio', body: 'Plain questions, grounded answers from your own data.', tint: 'text-primary' }
	];

	const extractedFields = [
		{ label: 'Document', value: 'Residential lease', conf: 0.98 },
		{ label: 'Tenant', value: 'Marcus Reyes', conf: 0.96 },
		{ label: 'Monthly rent', value: '$1,850.00', conf: 0.99 },
		{ label: 'Start date', value: 'Jul 1, 2026', conf: 0.94 },
		{ label: 'Security deposit', value: '$1,850.00', conf: 0.91 }
	];

	// --- Mobile section -------------------------------------------------------
	// The phone frame runs a self-contained capture -> extract -> confirm loop in
	// CSS. These are the fields that "fly in" on the device screen — a receipt,
	// which is the most phone-native capture (snap it the moment you're handed it).
	const phoneReceiptFields = [
		{ label: 'Vendor', value: "Hank's Plumbing", conf: 0.97 },
		{ label: 'Amount', value: '$284.50', conf: 0.99 },
		{ label: 'Date', value: 'Jun 3, 2026', conf: 0.95 },
		{ label: 'Category', value: 'Repairs', conf: 0.92 }
	];

	// Two mobile pillars. "Capture" is LIVE today. "Voice" is forthcoming
	// (ties to the Voice / App Actions task) — framed strictly as coming soon.
	const mobilePillars = [
		{
			icon: Camera,
			title: 'The computer does the typing for you',
			body: 'Photograph a receipt, lease, or check. The app reads it, pulls the fields with a confidence score, and hands you a draft to confirm. Live today.',
			status: 'live' as const
		},
		{
			icon: Mic,
			title: 'Say it and it happens',
			body: '“Log a $40 repair on Maple St.” Speak the command and the app does the rest — no tapping through screens. Coming soon.',
			status: 'soon' as const
		}
	];
</script>

<svelte:head>
	<title>Rental Command — the computer does the typing for you</title>
	<meta
		name="description"
		content="Scan a lease, invoice, or receipt and let AI extract the details. Rental Command turns documents into ready-to-confirm records, so small landlords can run everything from their phone."
	/>
</svelte:head>

<!--
  TODO (Remotion): a pre-rendered hero/feature animation can drop in here later.
  Per TODO #15, author the animation in a small Remotion project, render to MP4/WebM,
  and replace the CSS/SVG "scan animation" block below with:
    <video src="/marketing/hero.webm" poster="/marketing/hero.jpg" autoplay muted loop playsinline />
  Keep the prefers-reduced-motion fallback (show the poster, no autoplay).
-->

<div class="h-full overflow-y-auto bg-background text-foreground">
	<!-- Top bar -->
	<header
		class="sticky top-0 z-40 border-b border-border/60 bg-background/80 backdrop-blur supports-[backdrop-filter]:bg-background/60"
	>
		<div class="mx-auto flex h-16 max-w-6xl items-center justify-between px-5 sm:px-8">
			<a href="/welcome" class="flex items-center gap-2">
				<span
					class="flex h-8 w-8 items-center justify-center rounded-lg bg-primary/10 text-primary ring-1 ring-inset ring-primary/20"
				>
					<Building class="h-4 w-4" />
				</span>
				<span class="text-base font-semibold tracking-tight">Rental Command</span>
			</a>
			<div class="flex items-center gap-2">
				<Button href="/login" variant="ghost" size="sm" class="hidden sm:inline-flex">Sign in</Button>
				<Button href="/register" size="sm">
					Get started
					<ArrowRight class="h-4 w-4" />
				</Button>
			</div>
		</div>
	</header>

	<!-- ===================== HERO ===================== -->
	<section class="relative overflow-hidden">
		<!-- Ambient background -->
		<div
			class="animate-aurora pointer-events-none absolute inset-0 -z-10 opacity-70"
			style="background-image: radial-gradient(45% 55% at 18% 12%, color-mix(in oklab, var(--primary) 22%, transparent), transparent), radial-gradient(45% 50% at 85% 30%, color-mix(in oklab, var(--accent) 18%, transparent), transparent);"
			aria-hidden="true"
		></div>
		<div
			class="pointer-events-none absolute inset-0 -z-10 opacity-[0.05]"
			style="background-image: linear-gradient(var(--border) 1px, transparent 1px), linear-gradient(90deg, var(--border) 1px, transparent 1px); background-size: 44px 44px; mask-image: radial-gradient(70% 60% at 50% 30%, black, transparent);"
			aria-hidden="true"
		></div>

		<div class="mx-auto grid max-w-6xl items-center gap-12 px-5 py-20 sm:px-8 lg:grid-cols-2 lg:py-28">
			<!-- Copy -->
			<div use:reveal>
				<span
					class="inline-flex items-center gap-2 rounded-full border border-primary/20 bg-primary/10 px-3 py-1 text-xs font-medium text-primary"
				>
					<Sparkles class="h-3.5 w-3.5" />
					AI-powered property management
				</span>
				<h1 class="mt-5 text-4xl font-semibold leading-[1.05] tracking-tight sm:text-5xl lg:text-6xl">
					The computer does the
					<span class="relative whitespace-nowrap text-primary">
						typing
						<svg
							class="absolute -bottom-1 left-0 h-2 w-full text-primary/40"
							viewBox="0 0 100 8"
							preserveAspectRatio="none"
							aria-hidden="true"
						>
							<path d="M0 5 Q 25 1 50 4 T 100 3" fill="none" stroke="currentColor" stroke-width="2" />
						</svg>
					</span>
					for you.
				</h1>
				<p class="mt-6 max-w-md text-lg leading-relaxed text-muted-foreground">
					Snap a photo of a lease, invoice, or receipt — or just talk. Rental Command reads it,
					extracts the details, and hands you a ready-to-confirm draft. No more retyping.
				</p>
				<div class="mt-8 flex flex-wrap items-center gap-3">
					<Button href="/register" size="lg" class="h-11">
						Start free
						<ArrowRight class="h-4 w-4" />
					</Button>
					<Button href="/login" variant="outline" size="lg" class="h-11">Sign in</Button>
				</div>
				<div class="mt-6 flex flex-wrap items-center gap-x-5 gap-y-2 text-xs text-muted-foreground">
					<span class="inline-flex items-center gap-1.5"><Check class="h-3.5 w-3.5 text-success" /> No credit card</span>
					<span class="inline-flex items-center gap-1.5"><Check class="h-3.5 w-3.5 text-success" /> Built for phones</span>
					<span class="inline-flex items-center gap-1.5"><Check class="h-3.5 w-3.5 text-success" /> Your data stays yours</span>
				</div>
			</div>

			<!-- Animated scan demo (the creative centerpiece; CSS/SVG only) -->
			<div class="relative" use:reveal={120}>
				<div
					class="animate-float-slow relative mx-auto max-w-md rounded-2xl border border-border bg-card/80 p-4 shadow-2xl backdrop-blur"
				>
					<!-- "Document" being scanned -->
					<div class="relative overflow-hidden rounded-xl border border-border bg-secondary/40 p-5">
						<!-- scan line sweep -->
						<div
							class="animate-scan-sweep pointer-events-none absolute inset-x-3 z-10 h-px bg-primary shadow-[0_0_18px_2px_var(--primary)]"
							aria-hidden="true"
						></div>
						<div class="flex items-center gap-2 text-xs font-medium text-muted-foreground">
							<FileText class="h-4 w-4 text-primary" />
							lease-agreement.pdf
						</div>
						<div class="mt-4 space-y-2" aria-hidden="true">
							<div class="h-2.5 w-3/4 rounded bg-muted"></div>
							<div class="h-2.5 w-full rounded bg-muted"></div>
							<div class="h-2.5 w-5/6 rounded bg-muted"></div>
							<div class="h-2.5 w-2/3 rounded bg-muted"></div>
							<div class="h-2.5 w-full rounded bg-muted"></div>
							<div class="h-2.5 w-1/2 rounded bg-muted"></div>
						</div>
						<div
							class="mt-4 inline-flex items-center gap-1.5 rounded-full bg-primary/10 px-2.5 py-1 text-[11px] font-medium text-primary"
						>
							<ScanLine class="h-3.5 w-3.5" />
							Extracting fields…
						</div>
					</div>

					<!-- Extracted draft -->
					<div class="mt-4 rounded-xl border border-border bg-background p-4">
						<div class="mb-3 flex items-center justify-between">
							<span class="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
								Draft · Lease
							</span>
							<span
								class="inline-flex items-center gap-1 rounded-full bg-green-500/10 px-2 py-0.5 text-[10px] font-medium text-green-500"
							>
								<Sparkles class="h-3 w-3" /> AI
							</span>
						</div>
						<dl class="space-y-2.5">
							{#each extractedFields as f, i}
								<div use:reveal={200 + i * 90} class="flex items-center justify-between gap-3">
									<dt class="text-xs text-muted-foreground">{f.label}</dt>
									<dd class="flex items-center gap-2">
										<span class="text-sm font-medium tabular-nums text-foreground">{f.value}</span>
										<span
											class="h-1.5 w-10 overflow-hidden rounded-full bg-muted"
											title={`${Math.round(f.conf * 100)}% confidence`}
											aria-hidden="true"
										>
											<span
												class="block h-full rounded-full"
												style={`width:${f.conf * 100}%; background:${f.conf > 0.95 ? 'var(--success)' : 'var(--warning)'}`}
											></span>
										</span>
									</dd>
								</div>
							{/each}
						</dl>
						<Button class="mt-4 w-full" size="sm">
							<Check class="h-4 w-4" /> Confirm draft
						</Button>
					</div>
				</div>

				<!-- floating capture chips -->
				<div
					class="animate-float absolute -left-3 top-8 hidden items-center gap-2 rounded-xl border border-border bg-card px-3 py-2 text-xs shadow-lg sm:flex"
					style="animation-delay: -1.5s"
				>
					<Camera class="h-4 w-4 text-chart-3" /> Photo
				</div>
				<div
					class="animate-float absolute -right-3 bottom-16 hidden items-center gap-2 rounded-xl border border-border bg-card px-3 py-2 text-xs shadow-lg sm:flex"
					style="animation-delay: -3s"
				>
					<Mic class="h-4 w-4 text-chart-4" /> Voice
				</div>
			</div>
		</div>
	</section>

	<!-- ===================== MOBILE STORY ===================== -->
	<!--
	  Sells the phone-first story: run the whole business from your phone.
	  The phone frame runs a self-contained capture -> extract -> confirm loop
	  (CSS `rc-phone-*` keyframes + staged field reveals; see app.css), with a
	  graceful end-state under prefers-reduced-motion. The on-screen UI is a
	  *representative mockup*, not a real device screenshot.

	  TODO(real-screenshots): swap the mock device screen for actual Galaxy-S22
	  app captures once available — drop them in /static/marketing/mobile/ and
	  render inside `.phone-screen` (keep the device frame + the voice teaser).
	  Keep the capture->draft->confirm framing (it's live); keep Voice as "coming soon".
	-->
	<section id="mobile" class="relative overflow-hidden border-t border-border/60" data-testid="mobile-section">
		<!-- Ambient background, mirrored from the hero for cohesion -->
		<div
			class="animate-aurora pointer-events-none absolute inset-0 -z-10 opacity-50"
			style="background-image: radial-gradient(40% 50% at 80% 8%, color-mix(in oklab, var(--primary) 18%, transparent), transparent), radial-gradient(45% 55% at 12% 90%, color-mix(in oklab, var(--chart-4) 14%, transparent), transparent);"
			aria-hidden="true"
		></div>

		<div class="mx-auto grid max-w-6xl items-center gap-14 px-5 py-20 sm:px-8 lg:grid-cols-[1.05fr_0.95fr] lg:py-28">
			<!-- Copy + pillars -->
			<div use:reveal>
				<span
					class="inline-flex items-center gap-2 rounded-full border border-chart-4/25 bg-chart-4/10 px-3 py-1 text-xs font-medium text-chart-4"
				>
					<Camera class="h-3.5 w-3.5" />
					Phone-first by design
				</span>
				<h2 class="mt-5 max-w-lg text-3xl font-semibold leading-[1.1] tracking-tight sm:text-4xl">
					Run the whole business
					<span class="text-primary">from your phone.</span>
				</h2>
				<p class="mt-5 max-w-md text-lg leading-relaxed text-muted-foreground">
					No desk, no spreadsheet, no retyping. Handed a receipt at the property? Snap it on the
					spot and you’re done. Rental Command was built thumb-first.
				</p>

				<div class="mt-9 space-y-4">
					{#each mobilePillars as pillar, i}
						<div
							use:reveal={i * 120}
							data-testid={pillar.status === 'live' ? 'mobile-pillar-capture' : 'mobile-pillar-voice'}
							class="group relative flex gap-4 rounded-2xl border border-border bg-card/70 p-5 backdrop-blur transition-colors hover:border-primary/40"
						>
							<div
								class="flex h-12 w-12 shrink-0 items-center justify-center rounded-xl bg-primary/10 text-primary ring-1 ring-inset ring-primary/20"
							>
								<pillar.icon class="h-5 w-5" />
							</div>
							<div class="min-w-0">
								<div class="flex flex-wrap items-center gap-2">
									<h3 class="text-base font-semibold tracking-tight">{pillar.title}</h3>
									{#if pillar.status === 'live'}
										<span
											class="inline-flex items-center gap-1 rounded-full bg-success/12 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-success ring-1 ring-inset ring-success/25"
										>
											<span class="h-1.5 w-1.5 animate-pulse rounded-full bg-success"></span>
											Live today
										</span>
									{:else}
										<span
											class="inline-flex items-center gap-1 rounded-full bg-chart-4/12 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-chart-4 ring-1 ring-inset ring-chart-4/25"
										>
											<Zap class="h-3 w-3" />
											Coming soon
										</span>
									{/if}
								</div>
								<p class="mt-1.5 text-sm leading-relaxed text-muted-foreground">{pillar.body}</p>
							</div>
						</div>
					{/each}
				</div>

				<div class="mt-8 flex flex-wrap items-center gap-3">
					<Button href="/register" size="lg" class="h-11" data-testid="mobile-cta-register">
						Try it on your phone
						<ArrowRight class="h-4 w-4" />
					</Button>
					<a
						href="/docs"
						class="inline-flex items-center gap-1 text-sm font-medium text-muted-foreground transition-colors hover:text-foreground"
						data-testid="mobile-link-docs"
					>
						See how it works
						<ChevronRight class="h-4 w-4" />
					</a>
				</div>
			</div>

			<!-- Animated phone frame -->
			<div class="relative flex justify-center" use:reveal={120}>
				<!-- soft glow under the device -->
				<div
					class="pointer-events-none absolute bottom-6 left-1/2 -z-10 h-40 w-64 -translate-x-1/2 rounded-full bg-primary/20 blur-3xl"
					aria-hidden="true"
				></div>

				<!-- Device -->
				<div
					class="phone-device animate-float-slow relative h-[560px] w-[270px] rounded-[2.75rem] border border-border bg-card p-2.5 shadow-2xl ring-1 ring-inset ring-white/5"
					data-testid="mobile-phone-frame"
					aria-label="Animated demo: snap a receipt and the app fills in a draft"
				>
					<!-- side buttons -->
					<span class="absolute -left-[3px] top-28 h-12 w-[3px] rounded-l bg-border" aria-hidden="true"></span>
					<span class="absolute -left-[3px] top-44 h-16 w-[3px] rounded-l bg-border" aria-hidden="true"></span>
					<span class="absolute -right-[3px] top-36 h-20 w-[3px] rounded-r bg-border" aria-hidden="true"></span>

					<!-- screen -->
					<div class="phone-screen relative h-full w-full overflow-hidden rounded-[2.25rem] bg-background">
						<!-- status bar -->
						<div class="flex items-center justify-between px-5 pt-3 text-[10px] font-medium text-muted-foreground">
							<span class="tabular-nums">9:41</span>
							<div class="flex items-center gap-1.5" aria-hidden="true">
								<Signal class="h-3 w-3" />
								<Wifi class="h-3 w-3" />
								<BatteryFull class="h-3.5 w-3.5" />
							</div>
						</div>

						<!-- app header -->
						<div class="flex items-center gap-2 px-5 pb-2 pt-2">
							<span class="flex h-6 w-6 items-center justify-center rounded-md bg-primary/15 text-primary">
								<Building class="h-3.5 w-3.5" />
							</span>
							<span class="text-xs font-semibold tracking-tight">New expense</span>
							<span class="ml-auto inline-flex items-center gap-1 rounded-full bg-primary/10 px-2 py-0.5 text-[9px] font-medium text-primary">
								<Sparkles class="h-2.5 w-2.5" /> AI
							</span>
						</div>

						<!-- STAGE 1: camera viewfinder (a receipt being captured) -->
						<div class="phone-stage phone-stage-capture absolute inset-x-3 top-[72px] bottom-3 rounded-2xl border border-border bg-secondary/40">
							<div class="relative flex h-full flex-col overflow-hidden rounded-2xl">
								<!-- viewfinder reticle corners -->
								<div class="pointer-events-none absolute inset-3 rounded-xl" aria-hidden="true">
									<span class="absolute left-0 top-0 h-5 w-5 rounded-tl-md border-l-2 border-t-2 border-primary/70"></span>
									<span class="absolute right-0 top-0 h-5 w-5 rounded-tr-md border-r-2 border-t-2 border-primary/70"></span>
									<span class="absolute bottom-0 left-0 h-5 w-5 rounded-bl-md border-b-2 border-l-2 border-primary/70"></span>
									<span class="absolute bottom-0 right-0 h-5 w-5 rounded-br-md border-b-2 border-r-2 border-primary/70"></span>
								</div>
								<!-- scan line -->
								<div
									class="phone-scanline pointer-events-none absolute inset-x-5 z-10 h-px bg-primary shadow-[0_0_18px_2px_var(--primary)]"
									aria-hidden="true"
								></div>
								<!-- the "receipt" in frame -->
								<div class="mx-auto mt-9 w-32 rotate-[-3deg] rounded-md bg-card p-3 shadow-lg ring-1 ring-border" aria-hidden="true">
									<div class="flex items-center gap-1 text-[8px] font-semibold text-muted-foreground">
										<Receipt class="h-2.5 w-2.5 text-primary" /> RECEIPT
									</div>
									<div class="mt-2 space-y-1.5">
										<div class="h-1.5 w-3/4 rounded bg-muted"></div>
										<div class="h-1.5 w-full rounded bg-muted"></div>
										<div class="h-1.5 w-2/3 rounded bg-muted"></div>
										<div class="h-1.5 w-5/6 rounded bg-muted"></div>
										<div class="mt-2 h-2 w-1/2 rounded bg-primary/30"></div>
									</div>
								</div>
								<!-- shutter -->
								<div class="mt-auto flex items-center justify-center pb-5">
									<span class="phone-shutter flex h-11 w-11 items-center justify-center rounded-full bg-primary text-primary-foreground ring-4 ring-primary/20">
										<Camera class="h-5 w-5" />
									</span>
								</div>
							</div>
						</div>

						<!-- STAGE 2: extracted draft (fields fly in with confidence) -->
						<div class="phone-stage phone-stage-draft absolute inset-x-3 top-[72px] bottom-3 rounded-2xl border border-border bg-background p-3">
							<div class="mb-2 flex items-center justify-between">
								<span class="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">Draft · Expense</span>
								<span class="inline-flex items-center gap-1 rounded-full bg-success/10 px-1.5 py-0.5 text-[9px] font-medium text-success">
									<Sparkles class="h-2.5 w-2.5" /> Extracted
								</span>
							</div>
							<dl class="space-y-2.5">
								{#each phoneReceiptFields as f, i}
									<div class="phone-field flex items-center justify-between gap-2" style={`--phone-field-i:${i}`}>
										<dt class="text-[11px] text-muted-foreground">{f.label}</dt>
										<dd class="flex items-center gap-2">
											<span class="text-xs font-medium tabular-nums">{f.value}</span>
											<span
												class="h-1.5 w-8 overflow-hidden rounded-full bg-muted"
												title={`${Math.round(f.conf * 100)}% confidence`}
												aria-hidden="true"
											>
												<span
													class="block h-full rounded-full"
													style={`width:${f.conf * 100}%; background:${f.conf > 0.95 ? 'var(--success)' : 'var(--warning)'}`}
												></span>
											</span>
										</dd>
									</div>
								{/each}
							</dl>
							<div class="phone-confirm mt-3 flex w-full items-center justify-center gap-1.5 rounded-lg bg-primary py-2 text-xs font-semibold text-primary-foreground">
								<Check class="h-3.5 w-3.5" /> Confirm draft
							</div>
							<!-- STAGE 3: success toast -->
							<div class="phone-toast absolute inset-x-3 bottom-3 flex items-center gap-2 rounded-xl border border-success/30 bg-[color-mix(in_oklab,var(--success)_10%,var(--card))] px-3 py-2.5 shadow-lg">
								<span class="flex h-6 w-6 items-center justify-center rounded-full bg-success/20 text-success">
									<FileCheck2 class="h-3.5 w-3.5" />
								</span>
								<span class="text-[11px] font-medium text-foreground">Expense saved to Maple St.</span>
							</div>
						</div>
					</div>

					<!-- notch -->
					<div class="pointer-events-none absolute left-1/2 top-2.5 h-4 w-20 -translate-x-1/2 rounded-full bg-card ring-1 ring-inset ring-border" aria-hidden="true"></div>
				</div>

				<!-- Voice teaser mini-mock — clearly forthcoming -->
				<div
					class="animate-float absolute -bottom-2 -left-3 w-52 rounded-2xl border border-chart-4/30 bg-card/95 p-3 shadow-xl backdrop-blur sm:-left-8"
					style="animation-delay: -2s"
					data-testid="mobile-voice-teaser"
				>
					<div class="flex items-center gap-2">
						<span class="flex h-7 w-7 items-center justify-center rounded-full bg-chart-4/15 text-chart-4">
							<Mic class="h-3.5 w-3.5" />
						</span>
						<span class="text-[10px] font-semibold uppercase tracking-wide text-chart-4">Voice · Coming soon</span>
					</div>
					<p class="mt-2 rounded-lg bg-secondary/60 px-2.5 py-1.5 text-[11px] italic leading-snug text-foreground">
						“Log a $40 repair on Maple St.”
					</p>
					<div class="mt-1.5 flex items-center gap-1 pl-1 text-[10px] text-muted-foreground">
						<ChevronRight class="h-3 w-3 text-chart-4" /> Expense drafted &amp; ready
					</div>
				</div>
			</div>
		</div>
	</section>

	<!-- ===================== HOW IT WORKS ===================== -->
	<section class="border-t border-border/60 bg-card/30">
		<div class="mx-auto max-w-6xl px-5 py-20 sm:px-8">
			<div class="mx-auto max-w-2xl text-center" use:reveal>
				<span class="text-sm font-semibold uppercase tracking-wide text-primary">How it works</span>
				<h2 class="mt-3 text-3xl font-semibold tracking-tight sm:text-4xl">
					Scan → draft → confirm
				</h2>
				<p class="mt-4 text-muted-foreground">
					Three steps replace the data entry. You stay in control — nothing is saved until you say so.
				</p>
			</div>

			<div class="relative mt-14 grid gap-6 md:grid-cols-3">
				<!-- connecting line -->
				<div
					class="absolute left-0 right-0 top-7 hidden h-px bg-gradient-to-r from-transparent via-border to-transparent md:block"
					aria-hidden="true"
				></div>
				{#each steps as step, i}
					<div use:reveal={i * 120} class="relative rounded-2xl border border-border bg-card p-6">
						<div
							class="flex h-14 w-14 items-center justify-center rounded-xl bg-primary/10 text-primary ring-1 ring-inset ring-primary/20"
						>
							<step.icon class="h-6 w-6" />
						</div>
						<div class="mt-5 flex items-center gap-2">
							<span class="text-xs font-bold text-primary">0{i + 1}</span>
							<h3 class="text-lg font-semibold tracking-tight">{step.title}</h3>
						</div>
						<p class="mt-2 text-sm leading-relaxed text-muted-foreground">{step.body}</p>
					</div>
				{/each}
			</div>
		</div>
	</section>

	<!-- ===================== FEATURES ===================== -->
	<section class="border-t border-border/60">
		<div class="mx-auto max-w-6xl px-5 py-20 sm:px-8">
			<div class="mx-auto max-w-2xl text-center" use:reveal>
				<span class="text-sm font-semibold uppercase tracking-wide text-primary">Everything in one place</span>
				<h2 class="mt-3 text-3xl font-semibold tracking-tight sm:text-4xl">
					A full management system, simple on the surface
				</h2>
				<p class="mt-4 text-muted-foreground">
					Run leases, money, maintenance, and tenant communication without juggling spreadsheets and paper.
				</p>
			</div>

			<div class="mt-14 grid gap-5 sm:grid-cols-2 lg:grid-cols-3">
				{#each features as feature, i}
					<div
						use:reveal={(i % 3) * 100}
						class="group rounded-2xl border border-border bg-card p-6 transition-colors hover:border-primary/40"
					>
						<div
							class="flex h-11 w-11 items-center justify-center rounded-xl bg-secondary ring-1 ring-inset ring-border transition-colors group-hover:bg-primary/10"
						>
							<feature.icon class="h-5 w-5 {feature.tint}" />
						</div>
						<h3 class="mt-4 text-base font-semibold tracking-tight">{feature.title}</h3>
						<p class="mt-1.5 text-sm leading-relaxed text-muted-foreground">{feature.body}</p>
					</div>
				{/each}
			</div>
		</div>
	</section>

	<!-- ===================== TRUST STRIP ===================== -->
	<section class="border-t border-border/60 bg-card/30">
		<div class="mx-auto flex max-w-6xl flex-wrap items-center justify-center gap-x-10 gap-y-4 px-5 py-10 text-sm text-muted-foreground sm:px-8" use:reveal>
			<span class="inline-flex items-center gap-2"><ShieldCheck class="h-4 w-4 text-success" /> Your data stays yours</span>
			<span class="inline-flex items-center gap-2"><Wallet class="h-4 w-4 text-chart-2" /> Built for 15–40 units</span>
			<span class="inline-flex items-center gap-2"><ScanLine class="h-4 w-4 text-primary" /> Works from your phone</span>
		</div>
	</section>

	<!-- ===================== CTA ===================== -->
	<section class="border-t border-border/60">
		<div class="mx-auto max-w-6xl px-5 py-20 sm:px-8">
			<div
				class="relative overflow-hidden rounded-3xl border border-primary/20 bg-gradient-to-br from-primary/10 via-card to-card p-10 text-center sm:p-16"
				use:reveal
			>
				<div
					class="animate-aurora pointer-events-none absolute inset-0 -z-10 opacity-50"
					style="background-image: radial-gradient(40% 60% at 30% 20%, color-mix(in oklab, var(--primary) 30%, transparent), transparent), radial-gradient(40% 60% at 80% 80%, color-mix(in oklab, var(--accent) 24%, transparent), transparent);"
					aria-hidden="true"
				></div>
				<h2 class="mx-auto max-w-xl text-3xl font-semibold tracking-tight sm:text-4xl">
					Stop typing. Start confirming.
				</h2>
				<p class="mx-auto mt-4 max-w-lg text-muted-foreground">
					Create your account and let Rental Command turn your documents into records.
				</p>
				<div class="mt-8 flex flex-wrap items-center justify-center gap-3">
					<Button href="/register" size="lg" class="h-11">
						Get started free
						<ArrowRight class="h-4 w-4" />
					</Button>
					<Button href="/login" variant="outline" size="lg" class="h-11">Sign in</Button>
				</div>
			</div>
		</div>
	</section>

	<!-- ===================== FOOTER ===================== -->
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
				<a href="/login" class="hover:text-foreground">Sign in</a>
				<a href="/register" class="hover:text-foreground">Create account</a>
			</div>
			<span class="text-xs">© {new Date().getFullYear()} Rental Command</span>
		</div>
	</footer>
</div>
