<script lang="ts">
	import { onMount } from 'svelte';
	import { Button } from '$lib/components/ui/button';
	import { ArrowRight, Sparkles, Play } from '@lucide/svelte';

	/**
	 * Hero: scroll-scrubbed video of an architectural house model exploding into
	 * an exploded-view, resolving onto the "Rental Command" logo (baked into the
	 * final second). The <video> element is scrubbed (currentTime ← scroll
	 * progress) rather than played — native decoding + the browser's GPU scaler
	 * keep it crisp (no JPEG/canvas re-compression), and the mp4 is re-encoded
	 * with dense keyframes (keyint=6) so arbitrary seeks stay smooth.
	 *
	 * Lenis smooths the scroll; GSAP's scrub eases the progress before it drives
	 * the seek and the overlay choreography. Muted, no audio track at all.
	 *
	 * Props:
	 *  - onSeeItWork: secondary-CTA handler (smooth-scrolls to the scan demo).
	 */
	let { onSeeItWork = () => {} }: { onSeeItWork?: () => void } = $props();

	const SRC = '/landing/house-scrub.mp4?v=sm';
	// Phones get the dedicated 9:16 portrait cut (full-bleed); tablet + desktop use the
	// landscape scrub clip. Swapped onto the element in onMount when a phone is detected.
	const PORTRAIT_SRC = '/landing/house-portrait.mp4?v=p1';
	const POSTER = '/landing/house-poster.jpg?v=nowm';

	let section: HTMLElement;
	let stage: HTMLElement;
	let video: HTMLVideoElement;
	let duration = 10;
	let isPhone = $state(false);

	// piecewise easing helpers for the overlay choreography
	const clamp01 = (n: number) => (n < 0 ? 0 : n > 1 ? 1 : n);
	const fadeIn = (p: number, a: number, b: number) => clamp01((p - a) / (b - a));
	const fadeOut = (p: number, a: number, b: number) => 1 - clamp01((p - a) / (b - a));

	function applyOverlay(p: number) {
		if (!stage) return;
		// Copy + CTAs present from the top (strong first impression), then ease away
		// near the end so the baked-in "Rental Command" logo reveal owns the final
		// beat before the page fades to the scan cinematic.
		const head = fadeOut(p, 0.4, 0.52);
		const cta = fadeOut(p, 0.4, 0.52);
		const cue = fadeOut(p, 0, 0.06);
		stage.style.setProperty('--head-op', head.toFixed(3));
		stage.style.setProperty('--head-y', ((1 - head) * -10).toFixed(2) + 'px');
		stage.style.setProperty('--cta-op', cta.toFixed(3));
		stage.style.setProperty('--cta-y', ((1 - cta) * -10).toFixed(2) + 'px');
		stage.style.setProperty('--cue-op', cue.toFixed(3));
		// fade the hero to the scan section's near-black at the very end so the scene
		// change reads as a smooth cut-to-dark instead of a hard light→dark seam
		stage.style.setProperty('--veil', fadeIn(p, 0.88, 1).toFixed(3));
	}

	function seek(t: number) {
		if (!video || video.readyState < 2) return;
		const dur = duration || video.duration || 10;
		const target = Math.max(0, Math.min(dur - 0.04, t));
		if (Math.abs((video.currentTime || 0) - target) > 0.02) {
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

		// Responsive device tiers — re-evaluated on resize so the experience switches
		// LIVE as the viewport changes (resizing a desktop window previews mobile):
		//   ≤768px              → PHONE:   the 9:16 portrait cut, autoplayed.
		//   touch & >768px      → TABLET:  the landscape clip, autoplayed (touch can't scrub).
		//   fine-pointer & >768 → DESKTOP: the landscape clip, scroll-scrubbed.
		const mqPhone = window.matchMedia('(max-width: 768px)');
		const mqReduce = window.matchMedia('(prefers-reduced-motion: reduce)');
		const mqCoarse = window.matchMedia('(pointer: coarse)');

		let teardown = () => {};
		let cur = '';

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
		const autoplay = () => {
			const play = () => video.play().catch(() => {});
			if (video.readyState >= 2) play();
			else video.addEventListener('canplay', play, { once: true });
		};

		const apply = () => {
			const mode = mqReduce.matches
				? 'reduce'
				: mqPhone.matches
					? 'phone'
					: mqCoarse.matches
						? 'tablet'
						: 'desktop';
			if (mode === cur) return;
			teardown();
			teardown = () => {};
			cur = mode;
			isPhone = mode === 'phone';
			applyOverlay(0);

			if (mode === 'phone' || mode === 'tablet') {
				setSrc(mode === 'phone'); // phone → portrait, tablet → landscape
				video.loop = false;
				autoplay(); // ends on the baked-in "Rental Command" logo and holds
				return;
			}

			setSrc(false); // landscape
			if (mode === 'reduce') {
				video.pause();
				const rest = () => {
					if (video.readyState >= 1) {
						duration = video.duration || 10;
						seek(0); // calm assembled-house frame
					} else requestAnimationFrame(rest);
				};
				rest();
				return;
			}

			// DESKTOP: prime the decoder, then scroll-scrub currentTime ← progress.
			video.play().then(() => {
				video.pause();
				video.currentTime = 0;
			}).catch(() => {});
			let st: { kill: () => void } | undefined;
			let cancelled = false;
			(async () => {
				const { gsap } = await import('gsap');
				const { ScrollTrigger } = await import('gsap/ScrollTrigger');
				if (cancelled) return;
				gsap.registerPlugin(ScrollTrigger);
				const trigger = ScrollTrigger.create({
					trigger: section,
					start: 'top top',
					end: 'bottom bottom',
					scrub: 1,
					onUpdate: (self) => {
						seek(self.progress * (duration || 10));
						applyOverlay(self.progress);
					}
				});
				st = trigger as unknown as { kill: () => void };
				ScrollTrigger.refresh();
			})();
			teardown = () => {
				cancelled = true;
				st?.kill();
			};
		};

		apply();
		let raf = 0;
		const onChange = () => {
			cancelAnimationFrame(raf);
			raf = requestAnimationFrame(apply);
		};
		mqPhone.addEventListener('change', onChange);
		mqReduce.addEventListener('change', onChange);
		mqCoarse.addEventListener('change', onChange);

		return () => {
			cancelAnimationFrame(raf);
			teardown();
			mqPhone.removeEventListener('change', onChange);
			mqReduce.removeEventListener('change', onChange);
			mqCoarse.removeEventListener('change', onChange);
			video.removeEventListener('loadedmetadata', onMeta);
		};
	});
</script>

<section bind:this={section} class="house-hero" class:is-phone={isPhone} data-testid="house-hero">
	<div bind:this={stage} class="stage">
		<!-- svelte-ignore a11y_media_has_caption -->
		<video
			bind:this={video}
			class="bg-video"
			src={SRC}
			poster={POSTER}
			muted
			playsinline
			preload="auto"
			aria-hidden="true"
		></video>

		<!-- legibility scrim: lifts the copy off the light studio backdrop -->
		<div class="scrim" aria-hidden="true"></div>

		<div class="overlay">
			<div class="copy">
				<span class="eyebrow">
					<Sparkles class="h-3.5 w-3.5" />
					AI-powered property command
				</span>
				<h1 class="headline">
					Scan anything.<br />
					<span class="accent">Command every property.</span>
				</h1>
				<p class="sub">
					Snap a receipt, lease, invoice, or application. Rental Command reads it, drafts the
					record, and routes it to the right unit — you just confirm.
				</p>
			</div>

			<div class="cta">
				<Button href="/register" size="lg" class="h-12 px-7 text-base">
					Start free
					<ArrowRight class="h-4 w-4" />
				</Button>
				<button type="button" class="ghost-cta" onclick={onSeeItWork}>
					<Play class="h-4 w-4" />
					Watch the scan-to-command demo
				</button>
			</div>
		</div>

		<div class="scroll-cue" aria-hidden="true">
			<span class="mouse"><span class="wheel"></span></span>
		</div>

		<div class="veil" aria-hidden="true"></div>
	</div>
</section>

<style>
	/* Tall scroll runway; the inner stage sticks for the duration → cinematic scrub. */
	.house-hero {
		position: relative;
		height: 320vh;
		background: #e9eaec; /* matches the studio backdrop so edges never flash */
	}
	.stage {
		position: sticky;
		top: 0;
		height: 100svh;
		overflow: hidden;
		--head-op: 1;
		--cta-op: 1;
		--cue-op: 1;
		--head-y: 0px;
		--cta-y: 0px;
		--veil: 0;
	}
	.bg-video {
		position: absolute;
		inset: 0;
		width: 100%;
		height: 100%;
		object-fit: cover;
		display: block;
		z-index: 0;
	}
	/* Soft scrim — light wash bottom + left so dark copy stays legible on grey. */
	.scrim {
		position: absolute;
		inset: 0;
		z-index: 2;
		pointer-events: none;
		background:
			radial-gradient(120% 90% at 22% 82%, rgba(244, 245, 247, 0.85), transparent 55%),
			linear-gradient(to top, rgba(240, 241, 243, 0.7), transparent 42%);
	}
	/* Cut-to-dark veil for the hero→scan scene change (driven by --veil). */
	.veil {
		position: absolute;
		inset: 0;
		z-index: 4;
		background: #07070b;
		opacity: var(--veil, 0);
		pointer-events: none;
	}

	.overlay {
		position: absolute;
		inset: 0;
		z-index: 3;
		display: flex;
		flex-direction: column;
		justify-content: flex-end;
		align-items: flex-start;
		gap: 1.5rem;
		padding: clamp(1.5rem, 5vw, 5rem);
		padding-bottom: clamp(3rem, 8vh, 6rem);
		max-width: 60rem;
	}
	.copy {
		opacity: var(--head-op);
		transform: translateY(var(--head-y));
		will-change: opacity, transform;
	}
	.eyebrow {
		display: inline-flex;
		align-items: center;
		gap: 0.5rem;
		font-size: 0.8rem;
		font-weight: 600;
		letter-spacing: 0.01em;
		color: #4f378b;
		background: rgba(164, 107, 255, 0.1);
		border: 1px solid rgba(164, 107, 255, 0.28);
		border-radius: 999px;
		padding: 0.35rem 0.85rem;
	}
	.headline {
		margin: 1.1rem 0 0;
		font-family: var(--m3-font-display, 'Funnel Display', system-ui), sans-serif;
		font-weight: 600;
		font-size: clamp(2.4rem, 6.4vw, 5rem);
		line-height: 1.02;
		letter-spacing: -0.03em;
		color: #0c0c12;
		text-shadow: 0 1px 30px rgba(233, 234, 236, 0.9);
	}
	.accent {
		color: #5b3fb0;
	}
	.sub {
		margin: 1.25rem 0 0;
		max-width: 32rem;
		font-size: clamp(1rem, 1.4vw, 1.18rem);
		line-height: 1.55;
		color: #2c2c36;
	}

	.cta {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		gap: 0.9rem;
		opacity: var(--cta-op);
		transform: translateY(var(--cta-y));
		will-change: opacity, transform;
	}
	.ghost-cta {
		display: inline-flex;
		align-items: center;
		gap: 0.5rem;
		font-size: 0.98rem;
		font-weight: 600;
		color: #0c0c12;
		background: transparent;
		border: none;
		cursor: pointer;
		padding: 0.5rem 0.25rem;
		transition: color 0.15s;
	}
	.ghost-cta:hover {
		color: #5b3fb0;
	}

	.scroll-cue {
		position: absolute;
		bottom: 1.5rem;
		left: 50%;
		transform: translateX(-50%);
		z-index: 3;
		opacity: var(--cue-op);
	}
	.mouse {
		display: block;
		width: 24px;
		height: 38px;
		border: 2px solid rgba(12, 12, 18, 0.45);
		border-radius: 14px;
		position: relative;
	}
	.wheel {
		position: absolute;
		top: 6px;
		left: 50%;
		width: 3px;
		height: 7px;
		border-radius: 2px;
		background: rgba(12, 12, 18, 0.6);
		transform: translateX(-50%);
		animation: cue-scroll 1.6s ease-in-out infinite;
	}
	@keyframes cue-scroll {
		0% { transform: translate(-50%, 0); opacity: 0; }
		30% { opacity: 1; }
		60% { transform: translate(-50%, 10px); opacity: 0; }
		100% { opacity: 0; }
	}

	/* Mobile/tablet: no scrub runway. STACK — the clip plays in a block at the top
	   (the full logo reveal intact), with the copy resting BELOW it, so nothing
	   overlays the baked-in "Rental Command" logo. */
	@media (max-width: 768px), (pointer: coarse) {
		.house-hero {
			height: auto;
		}
		.stage {
			position: static;
			height: auto;
			display: flex;
			flex-direction: column;
			overflow: visible;
		}
		.bg-video {
			position: static;
			width: 100%;
			height: auto;
			aspect-ratio: 16 / 9; /* tablet: landscape block */
			object-fit: cover;
		}
		.scrim,
		.scroll-cue,
		.veil {
			display: none;
		}
		.overlay {
			position: static;
			inset: auto;
			max-width: none;
			gap: 1.25rem;
			padding: clamp(1.75rem, 6vw, 3rem);
		}
		.copy,
		.cta {
			opacity: 1 !important;
			transform: none !important;
		}
	}

	/* Phone: the dedicated 9:16 portrait clip as the top block (capped so the copy
	   below stays in view); cover keeps the centered logo reveal intact. */
	.house-hero.is-phone .bg-video {
		aspect-ratio: 9 / 16;
		max-height: 62svh;
	}

	@media (prefers-reduced-motion: reduce) {
		.house-hero {
			height: 100svh;
		}
		.copy,
		.cta {
			opacity: 1 !important;
			transform: none !important;
		}
		.scroll-cue {
			display: none;
		}
		.wheel {
			animation: none;
		}
		.veil {
			opacity: 0 !important;
		}
	}
</style>
