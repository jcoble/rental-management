<!--
  CoachOverlay — the visual half of the coachmark/spotlight primitive (controller: coach.svelte.ts).

  When a coach is active it dims everything EXCEPT a rectangular gap around the spotlighted element,
  pulses an outline ring on it, and floats a plain-English callout next to it. The dim is drawn as four
  panels around the target rect (not a full overlay) so the target stays natively clickable — no
  pointer-events trickery — and clicking it (or Escape, or a dim panel, or navigating) dismisses.

  Mounted ONCE globally (in the protected layout) so any page's deep-link can drive it. z-index sits
  above page content but the overlay self-dismisses the instant the user activates the target, so a
  modal opened by that target (z-50 dialog) is never dimmed.
-->
<script lang="ts">
	import { afterNavigate } from '$app/navigation';
	import { X } from '@lucide/svelte';
	import { coachRect, coachLabel, activeCoachKey, dismissCoach } from '$lib/onboarding/coach.svelte';

	// Breathing room around the target so the ring + glow aren't flush against it.
	const PAD = 8;

	const rect = $derived(coachRect());
	const label = $derived(coachLabel());

	// Padded spotlight box (clamped to the viewport).
	const box = $derived.by(() => {
		if (!rect) return null;
		const top = Math.max(0, rect.top - PAD);
		const left = Math.max(0, rect.left - PAD);
		const width = rect.width + PAD * 2;
		const height = rect.height + PAD * 2;
		return { top, left, width, height };
	});

	// Place the callout below the target, or above it if there isn't room near the bottom. Clamp its
	// left edge so a right-aligned target (e.g. a "New …" button in a toolbar) doesn't push the bubble
	// off-screen.
	const CALLOUT_W = 320;
	const callout = $derived.by(() => {
		if (!box) return null;
		const vh = typeof window !== 'undefined' ? window.innerHeight : 800;
		const vw = typeof window !== 'undefined' ? window.innerWidth : 1200;
		const below = box.top + box.height + 12;
		const placeAbove = below > vh - 160;
		const maxLeft = Math.max(12, vw - CALLOUT_W - 12);
		return {
			top: placeAbove ? Math.max(12, box.top - 12) : below,
			placeAbove,
			left: Math.min(box.left, maxLeft),
		};
	});

	// Dismiss when the PAGE (pathname) changes — a coach belongs to the page that started it. A
	// query-only replace (e.g. CoachTrigger stripping ?coach) keeps the same pathname and must NOT
	// dismiss, or the spotlight would be torn down the instant it's armed.
	let lastPath: string | null = null;
	afterNavigate((nav) => {
		const path = nav.to?.url.pathname ?? null;
		if (lastPath !== null && path !== lastPath) dismissCoach();
		lastPath = path;
	});
</script>

{#if box}
	<!-- Four dim panels around the spotlight gap. They capture clicks → dismiss. The gap (the target)
	     is left uncovered so it stays interactive. aria-hidden: this is a visual scrim, the underlying
	     control keeps its own semantics. -->
	<div class="coach-root" data-testid="coach-overlay" aria-hidden="true">
		<button
			type="button"
			class="coach-dim"
			style="top:0; left:0; right:0; height:{box.top}px;"
			tabindex="-1"
			aria-label="Dismiss highlight"
			onclick={dismissCoach}
		></button>
		<button
			type="button"
			class="coach-dim"
			style="top:{box.top + box.height}px; left:0; right:0; bottom:0;"
			tabindex="-1"
			aria-label="Dismiss highlight"
			onclick={dismissCoach}
		></button>
		<button
			type="button"
			class="coach-dim"
			style="top:{box.top}px; left:0; width:{box.left}px; height:{box.height}px;"
			tabindex="-1"
			aria-label="Dismiss highlight"
			onclick={dismissCoach}
		></button>
		<button
			type="button"
			class="coach-dim"
			style="top:{box.top}px; left:{box.left + box.width}px; right:0; height:{box.height}px;"
			tabindex="-1"
			aria-label="Dismiss highlight"
			onclick={dismissCoach}
		></button>

		<!-- Pulsing ring around the target (non-interactive; pointer events pass through to the target). -->
		<div
			class="coach-ring"
			style="top:{box.top}px; left:{box.left}px; width:{box.width}px; height:{box.height}px;"
		></div>
	</div>

	<!-- Callout bubble: the ELI5 "do this here" caption. Sits above the dim, dismissible. -->
	{#if callout}
		<div
			class="coach-callout"
			style="top:{callout.top}px; left:{callout.left}px; transform:{callout.placeAbove ? 'translateY(-100%)' : 'none'};"
			data-testid="coach-callout"
			role="status"
		>
			<div class="coach-callout__body">
				{#if label}
					<p class="coach-callout__text">{label}</p>
				{:else}
					<p class="coach-callout__text">Here's the spot — go ahead.</p>
				{/if}
				<button
					type="button"
					class="coach-callout__dismiss"
					onclick={dismissCoach}
					data-testid="coach-dismiss"
				>
					<X class="h-3.5 w-3.5" />
					<span>Got it</span>
				</button>
			</div>
		</div>
	{/if}
{/if}

<svelte:window onkeydown={(e) => { if (e.key === 'Escape' && activeCoachKey()) dismissCoach(); }} />

<style>
	.coach-root {
		position: fixed;
		inset: 0;
		z-index: 60; /* above header (30) / sidebar (40) / tooltips (70 self-dismiss before modal=50) */
		pointer-events: none;
	}
	.coach-dim {
		position: fixed;
		margin: 0;
		padding: 0;
		border: 0;
		cursor: pointer;
		pointer-events: auto;
		/* A soft scrim that reads in both themes — darker in dark mode via the token-driven mix. */
		background: color-mix(in srgb, var(--m3c-scrim, #000) 58%, transparent);
		backdrop-filter: blur(1px);
		transition: background 150ms ease;
	}
	:global(.light) .coach-dim {
		background: color-mix(in srgb, var(--m3c-scrim, #1a1a1a) 42%, transparent);
	}
	.coach-ring {
		position: fixed;
		pointer-events: none;
		border-radius: var(--m3-shape-large, 0.75rem);
		box-shadow:
			0 0 0 2px var(--primary),
			0 0 0 6px color-mix(in srgb, var(--primary) 35%, transparent),
			0 0 22px 4px color-mix(in srgb, var(--primary) 45%, transparent);
		animation: coach-pulse 1.6s ease-in-out infinite;
	}
	.coach-callout {
		position: fixed;
		z-index: 61;
		max-width: min(320px, calc(100vw - 24px));
		pointer-events: auto;
	}
	.coach-callout__body {
		display: flex;
		flex-direction: column;
		gap: 0.5rem;
		border-radius: var(--m3-shape-large, 0.75rem);
		border: 1px solid color-mix(in srgb, var(--primary) 40%, var(--border));
		background: var(--card);
		color: var(--card-foreground);
		padding: 0.75rem 0.875rem;
		box-shadow: 0 10px 30px -8px color-mix(in srgb, var(--m3c-scrim, #000) 45%, transparent);
		margin-top: 8px;
	}
	.coach-callout__text {
		font-size: 0.8125rem;
		line-height: 1.4;
		color: var(--foreground);
	}
	.coach-callout__dismiss {
		align-self: flex-end;
		display: inline-flex;
		align-items: center;
		gap: 0.25rem;
		font-size: 0.75rem;
		font-weight: 600;
		color: var(--primary);
		border-radius: var(--m3-shape-full, 9999px);
		padding: 0.125rem 0.5rem;
		transition: background 120ms ease;
	}
	.coach-callout__dismiss:hover {
		background: color-mix(in srgb, var(--primary) 12%, transparent);
	}
	@keyframes coach-pulse {
		0%,
		100% {
			box-shadow:
				0 0 0 2px var(--primary),
				0 0 0 6px color-mix(in srgb, var(--primary) 30%, transparent),
				0 0 18px 3px color-mix(in srgb, var(--primary) 35%, transparent);
		}
		50% {
			box-shadow:
				0 0 0 2px var(--primary),
				0 0 0 9px color-mix(in srgb, var(--primary) 12%, transparent),
				0 0 26px 6px color-mix(in srgb, var(--primary) 55%, transparent);
		}
	}
	@media (prefers-reduced-motion: reduce) {
		.coach-ring {
			animation: none;
		}
	}
</style>
