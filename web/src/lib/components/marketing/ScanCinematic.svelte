<script lang="ts">
	import { onMount } from 'svelte';
	import { Camera, Sparkles, LayoutDashboard, FileText } from '@lucide/svelte';

	/**
	 * Section ② — the scan-to-command story. The video just PLAYS (autoplay loop) on
	 * every device — no scrubbing. Phones get the 9:16 portrait cut, tablet/desktop the
	 * landscape cut.
	 *
	 * DESKTOP: the section pins and the four captions choreograph in / hold on each card,
	 * then release to §③. MOBILE/touch: no pin — the clip sits in a block and the captions
	 * STACK below it as a normal vertical scroll. A vignette hides the de-logo'd watermark.
	 */
	const SRC = '/landing/scan-to-command-scrub.mp4?v=a3';
	const PORTRAIT_SRC = '/landing/scan-portrait.mp4?v=p1';
	const POSTER = '/landing/scan-poster.jpg?v=a';

	let section: HTMLElement;
	let stage: HTMLElement;
	let video: HTMLVideoElement;
	let duration = 10;
	let isPhone = $state(false);
	let compact = $state(false);

	function band(p: number, a: number, b: number, edge = 0.16): number {
		if (p <= a || p >= b) return 0;
		const x = (p - a) / (b - a);
		return Math.max(0, Math.min(1, Math.min(x / edge, (1 - x) / edge)));
	}

	function applyCaptions(p: number) {
		if (!stage) return;
		stage.style.setProperty('--c1', band(p, 0.0, 0.26).toFixed(3));
		stage.style.setProperty('--c2', band(p, 0.24, 0.5).toFixed(3));
		stage.style.setProperty('--c3', band(p, 0.48, 0.74).toFixed(3));
		stage.style.setProperty('--c4', band(p, 0.72, 1.0).toFixed(3));
	}

	function seek(t: number) {
		if (!video || video.readyState < 1) return;
		const dur = duration || video.duration || 10;
		const target = Math.max(0, Math.min(dur - 0.05, t));
		if (Math.abs((video.currentTime || 0) - target) > 0.03) {
			try {
				video.currentTime = target;
			} catch {
				/* not seekable yet */
			}
		}
	}

	onMount(() => {
		video.muted = true;
		video.playsInline = true;
		const onMeta = () => {
			duration = video.duration || 10;
		};
		video.addEventListener('loadedmetadata', onMeta);

		const mqPhone = window.matchMedia('(max-width: 768px)');
		const mqCompact = window.matchMedia('(max-width: 768px), (pointer: coarse)');
		const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

		const setSrc = (portrait: boolean) => {
			const want = portrait ? PORTRAIT_SRC : SRC;
			const base = want.split('?')[0];
			if (!(video.currentSrc || video.src || '').includes(base)) {
				video.src = want;
				if (portrait) video.removeAttribute('poster');
				else video.setAttribute('poster', POSTER);
				video.load();
			}
		};

		let st: { kill: () => void } | undefined;
		let cancelled = false;
		let cur = '';

		const applyMode = () => {
			isPhone = mqPhone.matches;
			compact = mqCompact.matches || reduce;
			setSrc(isPhone);

			if (reduce) {
				video.loop = false;
				const rest = () => {
					if (video.readyState >= 1) {
						duration = video.duration || 10;
						seek(0.55 * duration);
					} else requestAnimationFrame(rest);
				};
				rest();
				applyCaptions(0.85);
			} else {
				// loop while in view; play() is gated on visibility (IntersectionObserver below)
				video.loop = true;
			}

			const mode = compact ? 'stack' : 'scrub';
			if (mode === cur) return;
			cur = mode;
			st?.kill();
			st = undefined;
			if (mode === 'stack') return; // captions stack statically (CSS)

			applyCaptions(0);
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
					onUpdate: (self) => applyCaptions(self.progress)
				});
				st = t as unknown as { kill: () => void };
				ScrollTrigger.refresh();
			})();
		};

		applyMode();
		const onChange = () => applyMode();
		mqPhone.addEventListener('change', onChange);
		mqCompact.addEventListener('change', onChange);

		// Gate playback on visibility — start from the BEGINNING only once the section is
		// ~halfway into view (so it never appears mid-loop or at the end while scrolling
		// down), and reset when scrolled away.
		let io: IntersectionObserver | undefined;
		if (!reduce) {
			io = new IntersectionObserver(
				([e]) => {
					if (e.intersectionRatio >= 0.5) {
						if (video.paused) {
							try {
								video.currentTime = 0;
							} catch {
								/* not seekable yet */
							}
							video.play().catch(() => {});
						}
					} else if (!e.isIntersecting) {
						video.pause();
						try {
							video.currentTime = 0;
						} catch {
							/* ignore */
						}
					}
				},
				{ threshold: [0, 0.5] }
			);
			io.observe(video);
		}

		return () => {
			cancelled = true;
			st?.kill();
			io?.disconnect();
			mqPhone.removeEventListener('change', onChange);
			mqCompact.removeEventListener('change', onChange);
			video.removeEventListener('loadedmetadata', onMeta);
		};
	});

	const beats = [
		{ k: 'c1', icon: Camera, title: 'Snap a receipt.', body: 'Point your phone at any document — a receipt, lease, invoice, or application.' },
		{ k: 'c2', icon: Sparkles, title: 'AI reads every field.', body: 'Vendor, amount, date, property, unit, category — extracted and ready to confirm.' },
		{ k: 'c3', icon: LayoutDashboard, title: 'Routed into your command center.', body: 'It posts the expense, attaches the doc, and lands on the right unit — automatically.' },
		{ k: 'c4', icon: FileText, title: 'Every document. Every unit. Every dollar — in command.', body: 'Owner reports build themselves from records captured the moment work happened.' }
	];
</script>

<section bind:this={section} id="scan" class="scan-cine" class:is-phone={isPhone} class:compact data-testid="scan-cinematic">
	<div bind:this={stage} class="stage">
		<div class="media">
			<!-- svelte-ignore a11y_media_has_caption -->
			<video
				bind:this={video}
				class="bg-video"
				src={SRC}
				poster={POSTER}
				muted
				playsinline
				loop
				preload="auto"
				aria-hidden="true"
			></video>
			<div class="scrim" aria-hidden="true"></div>
			<div class="wm-cover" aria-hidden="true"></div>
		</div>

		<div class="caps">
			{#each beats as b (b.k)}
				<div class="cap" style={`--op:var(--${b.k},0)`}>
					<span class="cap-icon"><b.icon class="h-5 w-5" /></span>
					<h2 class="cap-title">{b.title}</h2>
					<p class="cap-body">{b.body}</p>
				</div>
			{/each}
		</div>
	</div>
</section>

<style>
	.scan-cine {
		position: relative;
		height: 520vh; /* tall runway → each caption pins for a beat */
		background: #07070b;
	}
	.stage {
		position: sticky;
		top: 0;
		height: 100svh;
		overflow: hidden;
		background: #07070b;
		--c1: 0;
		--c2: 0;
		--c3: 0;
		--c4: 0;
	}
	.media {
		position: absolute;
		inset: 0;
		z-index: 0;
	}
	.bg-video {
		position: absolute;
		inset: 0;
		width: 100%;
		height: 100%;
		object-fit: cover;
	}
	.scrim {
		position: absolute;
		inset: 0;
		z-index: 1;
		pointer-events: none;
		background:
			radial-gradient(120% 100% at 28% 60%, rgba(7, 7, 11, 0.72), transparent 60%),
			linear-gradient(to top, rgba(7, 7, 11, 0.85), transparent 45%),
			linear-gradient(to bottom, rgba(7, 7, 11, 0.5), transparent 30%);
	}
	.wm-cover {
		position: absolute;
		right: 0;
		bottom: 0;
		width: 44%;
		height: 42%;
		z-index: 1;
		pointer-events: none;
		background: radial-gradient(
			120% 120% at 90% 88%,
			rgba(7, 7, 11, 0.85),
			rgba(7, 7, 11, 0.38) 42%,
			transparent 64%
		);
	}

	.caps {
		position: absolute;
		inset: 0;
		z-index: 2;
		display: flex;
		align-items: flex-end;
		padding: clamp(1.5rem, 5vw, 5rem);
		padding-bottom: clamp(3rem, 10vh, 7rem);
	}
	.cap {
		position: absolute;
		left: clamp(1.5rem, 5vw, 5rem);
		right: clamp(1.5rem, 5vw, 5rem);
		bottom: clamp(3rem, 10vh, 7rem);
		max-width: 40rem;
		opacity: var(--op);
		transform: translateY(calc((1 - var(--op)) * 14px));
		will-change: opacity, transform;
	}
	.cap-icon {
		display: inline-flex;
		align-items: center;
		justify-content: center;
		width: 2.5rem;
		height: 2.5rem;
		border-radius: 0.85rem;
		background: rgba(164, 107, 255, 0.18);
		color: #d4bbfc;
		border: 1px solid rgba(164, 107, 255, 0.32);
		margin-bottom: 1rem;
	}
	.cap-title {
		font-family: var(--m3-font-display, 'Funnel Display', system-ui), sans-serif;
		font-weight: 600;
		font-size: clamp(1.9rem, 4.4vw, 3.4rem);
		line-height: 1.05;
		letter-spacing: -0.02em;
		color: #f6f5fb;
		text-shadow: 0 2px 30px rgba(0, 0, 0, 0.55);
	}
	.cap-body {
		margin-top: 1rem;
		max-width: 34rem;
		font-size: clamp(1rem, 1.4vw, 1.18rem);
		line-height: 1.55;
		color: #c9c6d6;
	}

	/* ---- MOBILE / touch: stack (no pin) ---- */
	@media (max-width: 768px), (pointer: coarse) {
		.scan-cine {
			height: auto;
		}
		.stage {
			position: static;
			height: auto;
			overflow: visible;
			display: flex;
			flex-direction: column;
			gap: clamp(1.5rem, 6vw, 2.25rem);
			padding: clamp(2rem, 8vw, 3.5rem) clamp(1.25rem, 5vw, 2.5rem);
		}
		.media {
			position: relative;
			inset: auto;
			width: 100%;
			aspect-ratio: 16 / 9;
			border-radius: 1rem;
			overflow: hidden;
		}
		.scan-cine.is-phone .media {
			aspect-ratio: 9 / 16;
			max-height: 64svh;
			margin: 0 auto;
		}
		.scrim {
			background: linear-gradient(to top, rgba(7, 7, 11, 0.4), transparent 40%);
		}
		.caps {
			position: static;
			inset: auto;
			display: flex;
			flex-direction: column;
			gap: 1.5rem;
			padding: 0;
		}
		.cap {
			position: static;
			inset: auto;
			left: auto;
			right: auto;
			bottom: auto;
			max-width: none;
			opacity: 1 !important;
			transform: none !important;
		}
		.cap-icon {
			width: 2.1rem;
			height: 2.1rem;
			margin-bottom: 0.6rem;
		}
	}

	@media (prefers-reduced-motion: reduce) {
		.cap {
			transform: none !important;
		}
	}
</style>
