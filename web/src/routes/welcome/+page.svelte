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
		Check
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
