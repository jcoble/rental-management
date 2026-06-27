<script lang="ts">
	import { onMount } from 'svelte';
	import {
		Sparkles,
		LayoutGrid,
		LayoutDashboard,
		FileSignature,
		Wallet,
		Wrench,
		Receipt
	} from '@lucide/svelte';

	/**
	 * Section ③ — "See the whole app."
	 *
	 * DESKTOP: a pinned, scroll-scrubbed look at the REAL product. A vertical tab rail sits
	 * far left; the big window on the right shows each view in full (object-fit: contain, so
	 * nothing is cropped — the app's dark chrome makes the letterbox invisible). The first
	 * tab is the command center (portfolio nav + a unit open); the active tab's caption sits
	 * up in the heading.
	 *
	 * MOBILE/touch: no pinned scrub (feels stuck on a phone) — the views STACK into a normal
	 * vertical scroll. Switches live on resize.
	 */
	const tabs = [
		{ key: 'command', label: 'Command center', icon: LayoutGrid, shot: '/landing/app/commandcenter.webp',
			blurb: 'The whole app — portfolio nav down the side, a unit open, every tool one click away.' },
		{ key: 'overview', label: 'Overview', icon: LayoutDashboard, shot: '/landing/app/overview.webp',
			blurb: 'A single unit at a glance — tenant, lease, rent status, open repairs, recent activity.' },
		{ key: 'lease', label: 'Lease', icon: FileSignature, shot: '/landing/app/lease.webp',
			blurb: 'Terms, deposit, and dates with the signed document attached.' },
		{ key: 'rent', label: 'Rent', icon: Wallet, shot: '/landing/app/rent.webp',
			blurb: 'The balance, every payment, and a check scanned straight onto the ledger.' },
		{ key: 'maintenance', label: 'Maintenance', icon: Wrench, shot: '/landing/app/maintenance.webp',
			blurb: 'A request becomes a tracked work order in seconds, receipts attached.' },
		{ key: 'expenses', label: 'Expenses', icon: Receipt, shot: '/landing/app/expenses.webp',
			blurb: 'Add or scan a receipt and it posts to the right unit automatically.' }
	];
	const N = tabs.length;

	let section: HTMLElement;
	let stage: HTMLElement;
	let active = $state(0);
	let compact = $state(false);

	const clamp01 = (n: number) => (n < 0 ? 0 : n > 1 ? 1 : n);

	function apply(p: number) {
		if (!stage) return;
		const f = clamp01(p) * N;
		const idx = Math.min(N - 1, Math.floor(f));
		if (idx !== active) active = idx;
		const local = clamp01(f - idx);
		for (let i = 0; i < N; i++) {
			let op = 0;
			if (i === idx) op = idx === N - 1 ? 1 : clamp01((1 - local) / 0.3);
			else if (i === idx + 1) op = clamp01((local - 0.7) / 0.3);
			stage.style.setProperty(`--op${i}`, op.toFixed(3));
		}
		stage.style.setProperty('--p', clamp01(p).toFixed(4));
	}

	onMount(() => {
		const mqCompact = window.matchMedia('(max-width: 768px), (pointer: coarse)');
		const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
		let st: { kill: () => void } | undefined;
		let cancelled = false;
		let cur = '';

		const applyMode = () => {
			const mode = mqCompact.matches || reduce ? 'compact' : 'scrub';
			if (mode === cur) return;
			cur = mode;
			compact = mqCompact.matches;
			st?.kill();
			st = undefined;
			if (mode === 'compact') return;
			apply(0);
			(async () => {
				const { gsap } = await import('gsap');
				const { ScrollTrigger } = await import('gsap/ScrollTrigger');
				if (cancelled || cur !== 'scrub') return;
				gsap.registerPlugin(ScrollTrigger);
				const t = ScrollTrigger.create({
					trigger: section,
					start: 'top top',
					end: 'bottom bottom',
					scrub: 0.6,
					onUpdate: (self) => apply(self.progress)
				});
				st = t as unknown as { kill: () => void };
				ScrollTrigger.refresh();
			})();
		};

		applyMode();
		const onChange = () => applyMode();
		mqCompact.addEventListener('change', onChange);
		return () => {
			cancelled = true;
			st?.kill();
			mqCompact.removeEventListener('change', onChange);
		};
	});
</script>

<section bind:this={section} id="command-center" class="cc" data-testid="scrub-tabs">
	<div bind:this={stage} class="stage">
		<div class="atmos" aria-hidden="true">
			<span class="glow glow-a"></span>
			<span class="glow glow-b"></span>
		</div>

		{#if compact}
			<!-- MOBILE: stacked vertical scroll (no pinned scrub) -->
			<div class="head head-c">
				<span class="eyebrow"><Sparkles class="h-3.5 w-3.5" /> The web app</span>
				<h2 class="title">See the whole thing.</h2>
			</div>
			<ol class="stacked" role="list">
				{#each tabs as t (t.key)}
					<li class="s-row">
						<div class="s-label"><span class="s-ic"><t.icon class="h-4 w-4" /></span>{t.label}</div>
						<div class="window s-win">
							<div class="chrome" aria-hidden="true">
								<span class="dots"><i></i><i></i><i></i></span>
								<span class="url">rentalcommand.app</span>
							</div>
							<div class="viewport viewport-c">
								<img class="s-shot" src={t.shot} alt={`${t.label} — Rental Command`} loading="lazy" decoding="async" />
							</div>
						</div>
						<p class="s-blurb">{t.blurb}</p>
					</li>
				{/each}
			</ol>
		{:else}
			<!-- DESKTOP: heading (with the active caption) + left tab rail + big window -->
			<div class="head">
				<span class="eyebrow"><Sparkles class="h-3.5 w-3.5" /> The web app</span>
				<h2 class="title">See the whole thing.</h2>
				<div class="blurbs">
					{#each tabs as t, i (t.key)}
						<p class="blurb" style={`opacity:var(--op${i},0)`}>{t.blurb}</p>
					{/each}
				</div>
			</div>

			<div class="body">
				<ol class="rail" role="tablist">
					{#each tabs as t, i (t.key)}
						<li role="presentation">
							<button type="button" role="tab" class="tab {active === i ? 'on' : ''}" aria-selected={active === i}>
								<span class="tab-ic"><t.icon class="h-4 w-4" /></span>
								<span>{t.label}</span>
							</button>
						</li>
					{/each}
				</ol>

				<div class="screen">
					<div class="window">
						<span class="win-glow" aria-hidden="true"></span>
						<div class="chrome" aria-hidden="true">
							<span class="dots"><i></i><i></i><i></i></span>
							<span class="url">rentalcommand.app</span>
						</div>
						<div class="viewport">
							{#each tabs as t, i (t.key)}
								<img
									class="shot"
									src={t.shot}
									alt={`${t.label} — Rental Command`}
									loading={i === 0 ? 'eager' : 'lazy'}
									decoding="async"
									style={`opacity:var(--op${i},0)`}
								/>
							{/each}
						</div>
					</div>
				</div>
			</div>
		{/if}
	</div>
</section>

<style>
	.cc {
		position: relative;
		height: 540vh; /* 6 tabs */
		background: #07070b;
	}
	.stage {
		position: sticky;
		top: 0;
		height: 100svh;
		overflow: hidden;
		display: flex;
		flex-direction: column;
		gap: clamp(0.75rem, 2vh, 1.5rem);
		padding: clamp(4.5rem, 8vh, 6rem) clamp(1.5rem, 5vw, 5rem) clamp(1.75rem, 4vh, 3rem);
		--p: 0;
	}
	.atmos {
		position: absolute;
		inset: 0;
		z-index: 0;
		pointer-events: none;
		overflow: hidden;
	}
	.glow {
		position: absolute;
		border-radius: 50%;
		filter: blur(80px);
	}
	.glow-a {
		top: -14%;
		right: -6%;
		width: 46vw;
		height: 46vw;
		background: radial-gradient(circle, rgba(124, 92, 255, 0.34), transparent 65%);
		opacity: 0.5;
		transform: translate3d(calc(var(--p) * -5vw), calc(var(--p) * 3vh), 0);
	}
	.glow-b {
		bottom: -16%;
		left: 8%;
		width: 34vw;
		height: 34vw;
		background: radial-gradient(circle, rgba(34, 211, 238, 0.2), transparent 65%);
		opacity: 0.5;
	}

	.head {
		position: relative;
		z-index: 1;
		max-width: 52rem;
	}
	.head-c {
		text-align: center;
		max-width: 44rem;
		margin: 0 auto;
	}
	.eyebrow {
		display: inline-flex;
		align-items: center;
		gap: 0.5rem;
		font-size: 0.8rem;
		font-weight: 600;
		color: #d4bbfc;
		background: rgba(164, 107, 255, 0.12);
		border: 1px solid rgba(164, 107, 255, 0.28);
		border-radius: 999px;
		padding: 0.3rem 0.8rem;
	}
	.title {
		margin: 0.6rem 0 0;
		font-family: var(--m3-font-display, 'Funnel Display', system-ui), sans-serif;
		font-weight: 600;
		font-size: clamp(1.7rem, 3.2vw, 2.7rem);
		line-height: 1.04;
		letter-spacing: -0.025em;
		color: #f6f5fb;
	}
	.blurbs {
		position: relative;
		min-height: 2.6rem;
		margin-top: 0.55rem;
		max-width: 44rem;
	}
	.blurb {
		position: absolute;
		inset: 0;
		margin: 0;
		font-size: clamp(0.95rem, 1.15vw, 1.1rem);
		line-height: 1.5;
		color: #b6b3c6;
		will-change: opacity;
	}

	/* ---- desktop body: left rail + big window ---- */
	.body {
		position: relative;
		z-index: 1;
		flex: 1;
		min-height: 0;
		width: 100%;
		max-width: 84rem;
		margin-inline: auto;
		display: flex;
		align-items: stretch;
		gap: clamp(1.25rem, 2.5vw, 2.5rem);
	}
	.rail {
		list-style: none;
		margin: 0;
		padding: 0;
		flex: none;
		width: clamp(11rem, 15vw, 13.5rem);
		display: flex;
		flex-direction: column;
		justify-content: center;
		gap: 0.3rem;
	}
	.tab {
		display: flex;
		align-items: center;
		gap: 0.6rem;
		width: 100%;
		padding: 0.6rem 0.75rem;
		border-radius: 0.7rem;
		border: 1px solid transparent;
		background: transparent;
		cursor: default;
		font-size: 0.92rem;
		font-weight: 600;
		color: #8b8aa0;
		text-align: left;
		transition: color 0.3s, background 0.3s, border-color 0.3s;
	}
	.tab.on {
		color: #f6f5fb;
		background: rgba(164, 107, 255, 0.16);
		border-color: rgba(164, 107, 255, 0.4);
		box-shadow: 0 0 18px rgba(164, 107, 255, 0.18);
	}
	.tab-ic {
		display: inline-flex;
		align-items: center;
		justify-content: center;
		width: 1.85rem;
		height: 1.85rem;
		flex: none;
		border-radius: 0.55rem;
		color: #8b8aa0;
		background: rgba(255, 255, 255, 0.04);
		border: 1px solid rgba(255, 255, 255, 0.08);
		transition: color 0.3s, background 0.3s, border-color 0.3s;
	}
	.tab.on .tab-ic {
		color: #fff;
		background: rgba(164, 107, 255, 0.28);
		border-color: rgba(164, 107, 255, 0.5);
	}

	.screen {
		flex: 1;
		min-width: 0;
		display: flex;
		align-items: center;
		justify-content: center;
	}
	.window {
		position: relative;
		width: 100%;
		max-width: 64rem;
		border-radius: 0.85rem;
		overflow: hidden;
		background: #08080d;
		border: 1px solid rgba(255, 255, 255, 0.12);
		box-shadow:
			0 50px 100px -42px rgba(0, 0, 0, 0.92),
			inset 0 1px 0 rgba(255, 255, 255, 0.06);
		transform: perspective(2400px) rotateX(calc(1.5deg - var(--p) * 1deg));
		transform-origin: center top;
	}
	.win-glow {
		position: absolute;
		inset: -20%;
		z-index: 0;
		pointer-events: none;
		background: radial-gradient(circle at 50% 0%, rgba(124, 92, 255, 0.26), transparent 60%);
		filter: blur(34px);
	}
	.chrome {
		position: relative;
		z-index: 1;
		display: flex;
		align-items: center;
		gap: 0.7rem;
		padding: 0.5rem 0.8rem;
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
		padding: 0.18rem 0.75rem;
	}
	.viewport {
		position: relative;
		z-index: 1;
		aspect-ratio: 1600 / 888; /* command-center ratio; others letterbox into the dark chrome */
		background: #08080d;
	}
	.shot {
		position: absolute;
		inset: 0;
		width: 100%;
		height: 100%;
		object-fit: contain; /* show every screen in full — no bottom crop */
		will-change: opacity;
	}

	/* ---- MOBILE stacked ---- */
	.viewport-c {
		aspect-ratio: 1600 / 994;
		background: #08080d;
	}
	.stacked {
		position: relative;
		z-index: 1;
		list-style: none;
		margin: 0.5rem 0 0;
		padding: 0;
		width: 100%;
		max-width: 42rem;
		display: flex;
		flex-direction: column;
		gap: clamp(2rem, 7vw, 3rem);
	}
	.s-row {
		display: flex;
		flex-direction: column;
		gap: 0.7rem;
	}
	.s-label {
		display: inline-flex;
		align-items: center;
		gap: 0.55rem;
		font-size: 1rem;
		font-weight: 600;
		color: #f6f5fb;
	}
	.s-ic {
		display: inline-flex;
		align-items: center;
		justify-content: center;
		width: 1.9rem;
		height: 1.9rem;
		border-radius: 0.6rem;
		color: #c9acff;
		background: rgba(164, 107, 255, 0.16);
		border: 1px solid rgba(164, 107, 255, 0.34);
	}
	.s-win {
		transform: none;
	}
	.s-shot {
		position: absolute;
		inset: 0;
		width: 100%;
		height: 100%;
		object-fit: cover;
		object-position: top center;
	}
	.s-blurb {
		margin: 0;
		font-size: 0.92rem;
		line-height: 1.5;
		color: #b6b3c6;
	}

	@media (max-width: 768px), (pointer: coarse) {
		.cc {
			height: auto;
		}
		.stage {
			position: static;
			height: auto;
			min-height: 0;
			overflow: visible;
			align-items: center;
			padding-top: clamp(3rem, 9vw, 4.5rem);
			padding-bottom: clamp(3rem, 9vw, 4.5rem);
		}
	}

	@media (prefers-reduced-motion: reduce) {
		.window {
			transform: none !important;
		}
		.glow-a {
			transform: none !important;
		}
		.tab,
		.tab-ic {
			transition: none;
		}
	}
</style>
