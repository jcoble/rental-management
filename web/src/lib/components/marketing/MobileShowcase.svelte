<script lang="ts">
	import { onMount } from 'svelte';

	/**
	 * Section ④ — "Your whole portfolio, in your pocket."
	 *
	 * DESKTOP: a pinned, scroll-scrubbed phone (with two ghost phones behind for depth)
	 * whose screen crossfades through real app shots while a big caption advances —
	 * briefing → capture → money → work → insights → "everything else" (the breadth, so
	 * §③ stays honest as just one surface).
	 *
	 * MOBILE/touch: no pin — the same screens STACK into a normal vertical scroll.
	 */
	const beats = [
		{ key: 'briefing', shot: '/landing/mobile/home-briefing.webp', eyebrow: 'Every morning',
			title: 'Wake up to a briefing.',
			body: 'Your AI reads the whole portfolio overnight and surfaces what needs you first — overdue rent, expiring leases, the repairs that can’t wait.' },
		{ key: 'capture', shot: '/landing/mobile/capture.webp', eyebrow: 'On the spot',
			title: 'Capture it any way you like.',
			body: 'Snap a receipt, photograph a lease, drop in a PDF, record a voice note, or just type it. The app reads it and drafts the record.' },
		{ key: 'money', shot: '/landing/mobile/payments.webp', eyebrow: 'Always current',
			title: 'Money, reconciled in your pocket.',
			body: 'Collected, outstanding, overdue — and exactly who’s behind. Mark a payment the moment the check clears, from anywhere.' },
		{ key: 'work', shot: '/landing/mobile/work-orders.webp', eyebrow: 'Request to done',
			title: 'Repairs, dispatched on the walk.',
			body: 'Turn a tenant’s text into a tracked work order across any property — priority, vendor, and photos, right from the hallway.' },
		{ key: 'insights', shot: '/landing/mobile/insights.webp', eyebrow: 'The whole picture',
			title: 'Know your numbers, live.',
			body: 'Occupancy, collection rate, income against expenses, leases expiring — your portfolio’s vitals, refreshed in real time.' },
		{ key: 'more', shot: '/landing/mobile/browse.webp', eyebrow: 'And everything else',
			title: 'The whole system — a tap away.',
			body: 'Properties, tenants, leases, applications, inspections, recurring maintenance. A single unit’s command center is just the start.' }
	];
	const N = beats.length;
	const ghostA = '/landing/mobile/messages.webp';
	const ghostB = '/landing/mobile/deposits.webp';

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
			const mode = mqCompact.matches || reduce ? 'stack' : 'scrub';
			if (mode === cur) return;
			cur = mode;
			compact = mqCompact.matches || reduce;
			st?.kill();
			st = undefined;
			if (mode === 'stack') return;
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

<section bind:this={section} id="mobile" class="mob" class:compact data-testid="mobile-showcase">
	<div bind:this={stage} class="stage">
		<div class="atmos" aria-hidden="true">
			<span class="glow glow-static"></span>
			<span class="glow glow-drift"></span>
		</div>

		{#if compact}
			<!-- MOBILE: stacked vertical scroll -->
			<div class="m-head">
				<span class="kicker">In your pocket</span>
				<h2 class="m-title">Your whole portfolio, on the phone.</h2>
			</div>
			<ol class="m-stacked" role="list">
				{#each beats as b (b.key)}
					<li class="m-row">
						<div class="phone m-phone">
							<div class="screen"><img src={b.shot} alt={`${b.title} — Rental Command mobile`} loading="lazy" decoding="async" /></div>
						</div>
						<div class="m-cap">
							<span class="eyebrow">{b.eyebrow}</span>
							<h3 class="m-cap-title">{b.title}</h3>
							<p class="m-cap-body">{b.body}</p>
						</div>
					</li>
				{/each}
			</ol>
		{:else}
			<!-- DESKTOP: pinned phone cluster -->
			<div class="wrap">
				<div class="copy">
					<span class="kicker">Web + mobile, one login</span>
					<div class="beats">
						{#each beats as b, i (b.key)}
							<div class="beat" style={`opacity:var(--op${i},0)`}>
								<span class="eyebrow">{b.eyebrow}</span>
								<h2 class="title">{b.title}</h2>
								<p class="body">{b.body}</p>
							</div>
						{/each}
					</div>
					<div class="meter">
						<span class="count">{String(active + 1).padStart(2, '0')} <span class="of">/ {String(N).padStart(2, '0')}</span></span>
						<span class="bar"><span class="fill" style={`transform:scaleX(var(--p,0))`}></span></span>
					</div>
				</div>

				<div class="cluster">
					<div class="phone ghost ghost-a"><div class="screen"><img src={ghostA} alt="" loading="lazy" decoding="async" /></div></div>
					<div class="phone ghost ghost-b"><div class="screen"><img src={ghostB} alt="" loading="lazy" decoding="async" /></div></div>
					<div class="phone hero">
						<span class="phone-glow" aria-hidden="true"></span>
						<div class="screen">
							{#each beats as b, i (b.key)}
								<img class="shot" src={b.shot} alt={`${b.title} — Rental Command on mobile`} width="720" height="1560" loading="eager" decoding="async" style={`opacity:var(--op${i},0)`} />
							{/each}
						</div>
					</div>
				</div>
			</div>
		{/if}
	</div>
</section>

<style>
	.mob {
		position: relative;
		height: 560vh;
		background: #07070b;
	}
	.stage {
		position: sticky;
		top: 0;
		height: 100svh;
		overflow: hidden;
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
		opacity: 0.5;
	}
	.glow-static {
		top: -10%;
		right: -5%;
		width: 48vw;
		height: 48vw;
		background: radial-gradient(circle, rgba(124, 92, 255, 0.45), transparent 65%);
	}
	.glow-drift {
		bottom: -15%;
		left: -10%;
		width: 42vw;
		height: 42vw;
		background: radial-gradient(circle, rgba(34, 211, 238, 0.3), transparent 65%);
		transform: translate3d(calc(var(--p) * 36vw), calc(var(--p) * -26vh), 0);
	}

	.wrap {
		position: relative;
		z-index: 1;
		height: 100%;
		max-width: 78rem;
		margin: 0 auto;
		padding: clamp(4.75rem, 9vh, 6.5rem) clamp(1.5rem, 5vw, 5rem) clamp(2rem, 5vh, 4rem);
		display: grid;
		grid-template-columns: 1fr 0.92fr;
		align-items: center;
		gap: clamp(1.5rem, 4vw, 4rem);
	}
	.copy {
		position: relative;
		max-width: 34rem;
	}
	.kicker {
		display: inline-flex;
		align-items: center;
		font-size: 0.78rem;
		font-weight: 600;
		letter-spacing: 0.08em;
		text-transform: uppercase;
		color: #8f8da4;
		margin-bottom: 1.5rem;
	}
	.beats {
		position: relative;
		min-height: 16rem;
	}
	.beat {
		position: absolute;
		inset: 0;
		will-change: opacity;
	}
	.eyebrow {
		display: inline-block;
		font-size: 0.82rem;
		font-weight: 700;
		letter-spacing: 0.04em;
		text-transform: uppercase;
		color: #c9acff;
		margin-bottom: 0.9rem;
	}
	.title {
		margin: 0;
		font-family: var(--m3-font-display, 'Funnel Display', system-ui), sans-serif;
		font-weight: 600;
		font-size: clamp(2.1rem, 4.6vw, 3.6rem);
		line-height: 1.03;
		letter-spacing: -0.025em;
		color: #f6f5fb;
		text-shadow: 0 2px 40px rgba(0, 0, 0, 0.5);
	}
	.body {
		margin: 1.1rem 0 0;
		max-width: 30rem;
		font-size: clamp(1rem, 1.3vw, 1.18rem);
		line-height: 1.6;
		color: #b6b3c6;
	}
	.meter {
		position: absolute;
		bottom: -4.5rem;
		left: 0;
		right: 0;
		display: flex;
		align-items: center;
		gap: 1rem;
	}
	.count {
		font-family: var(--m3-font-display, 'Funnel Display', system-ui), sans-serif;
		font-size: 1.05rem;
		font-weight: 600;
		color: #f6f5fb;
		font-variant-numeric: tabular-nums;
		white-space: nowrap;
	}
	.count .of {
		color: #6f6e84;
	}
	.bar {
		position: relative;
		flex: 1;
		height: 2px;
		background: rgba(255, 255, 255, 0.1);
		border-radius: 2px;
		overflow: hidden;
	}
	.bar .fill {
		position: absolute;
		inset: 0;
		transform-origin: left;
		background: linear-gradient(90deg, #a36bff, #22d3ee);
		border-radius: 2px;
	}

	.cluster {
		position: relative;
		display: flex;
		align-items: center;
		justify-content: center;
		perspective: 1600px;
		height: 100%;
	}
	.phone {
		position: relative;
		width: clamp(208px, 23vw, 286px);
		aspect-ratio: 1080 / 2340;
		border-radius: 2.5rem;
		padding: 0.42rem;
		background: linear-gradient(155deg, #20202a, #0a0a0f 60%);
		box-shadow:
			0 50px 90px -34px rgba(0, 0, 0, 0.9),
			inset 0 0 0 1px rgba(255, 255, 255, 0.07);
	}
	.phone .screen {
		position: relative;
		height: 100%;
		border-radius: 2.15rem;
		overflow: hidden;
		background: #000;
	}
	.phone .screen img {
		position: absolute;
		inset: 0;
		width: 100%;
		height: 100%;
		object-fit: cover;
		object-position: top center;
	}
	.phone.hero {
		z-index: 3;
		transform: translateZ(0) rotateY(calc((var(--p) - 0.5) * -7deg)) rotateX(2deg)
			translateY(calc((0.5 - var(--p)) * 1.5rem));
		will-change: transform;
	}
	.hero .shot {
		will-change: opacity;
	}
	.phone-glow {
		position: absolute;
		inset: -14% -22%;
		z-index: -1;
		background: radial-gradient(circle, rgba(124, 92, 255, 0.42), transparent 62%);
		filter: blur(36px);
		pointer-events: none;
	}
	.phone.ghost {
		position: absolute;
		z-index: 1;
		opacity: 0.42;
		filter: blur(2px) saturate(0.8);
		width: clamp(168px, 18vw, 232px);
	}
	.ghost-a {
		transform: translate3d(calc(-46% + var(--p) * -3%), -7%, 0) rotate(-9deg);
	}
	.ghost-b {
		transform: translate3d(calc(46% + var(--p) * 3%), 9%, 0) rotate(8deg);
		z-index: 2;
	}

	/* ---- MOBILE stacked ---- */
	.m-head {
		position: relative;
		z-index: 1;
		max-width: 42rem;
		margin: 0 auto;
		text-align: center;
		padding: clamp(3rem, 9vw, 4.5rem) clamp(1.25rem, 5vw, 2.5rem) 0;
	}
	.m-title {
		margin: 0.5rem 0 0;
		font-family: var(--m3-font-display, 'Funnel Display', system-ui), sans-serif;
		font-weight: 600;
		font-size: clamp(1.9rem, 7vw, 2.6rem);
		line-height: 1.05;
		letter-spacing: -0.025em;
		color: #f6f5fb;
	}
	.m-stacked {
		position: relative;
		z-index: 1;
		list-style: none;
		margin: clamp(2rem, 8vw, 3rem) auto 0;
		padding: 0 clamp(1.25rem, 5vw, 2.5rem) clamp(2rem, 8vw, 3.5rem);
		max-width: 30rem;
		display: flex;
		flex-direction: column;
		gap: clamp(2.5rem, 10vw, 4rem);
	}
	.m-row {
		display: flex;
		flex-direction: column;
		align-items: center;
		gap: 1.25rem;
		text-align: center;
	}
	.m-phone {
		width: clamp(180px, 56vw, 230px);
	}
	.m-cap .eyebrow {
		margin-bottom: 0.5rem;
	}
	.m-cap-title {
		margin: 0;
		font-family: var(--m3-font-display, 'Funnel Display', system-ui), sans-serif;
		font-weight: 600;
		font-size: clamp(1.5rem, 6vw, 1.9rem);
		line-height: 1.1;
		letter-spacing: -0.02em;
		color: #f6f5fb;
	}
	.m-cap-body {
		margin: 0.6rem 0 0;
		font-size: 0.98rem;
		line-height: 1.55;
		color: #b6b3c6;
	}

	@media (max-width: 768px), (pointer: coarse) {
		.mob {
			height: auto;
		}
		.stage {
			position: static;
			height: auto;
			overflow: visible;
		}
	}

	@media (prefers-reduced-motion: reduce) {
		.glow-drift {
			transform: none !important;
		}
	}
</style>
