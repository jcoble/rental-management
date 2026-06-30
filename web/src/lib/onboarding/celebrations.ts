/**
 * Onboarding celebrations (TSK-599) — a small variety of "you did it!" rewards fired
 * when the landlord finishes an onboarding milestone (added a property, imported a
 * lease, etc.) and a bigger finale when the core setup is done. Setup should feel fun.
 *
 * Confetti / fireworks / stars use `canvas-confetti` (a tiny canvas overlay it manages
 * itself); balloons are lightweight DOM elements animated with the Web Animations API.
 * Everything no-ops under `prefers-reduced-motion` and on the server.
 */
import confetti from 'canvas-confetti';

/** Brand-ish palette: violet primary + the mint / coral / amber / teal accents. */
const COLORS = ['#7c6ff5', '#c6c0ff', '#a78bfa', '#34d399', '#ff8794', '#f5b544', '#2dd4d4'];

function canCelebrate(): boolean {
	return (
		typeof window !== 'undefined' &&
		typeof document !== 'undefined' &&
		!window.matchMedia('(prefers-reduced-motion: reduce)').matches
	);
}

/** A double confetti "cannon" from the lower corners, angled inward. */
export function confettiBurst(): void {
	if (!canCelebrate()) return;
	const common = { particleCount: 70, spread: 70, startVelocity: 55, colors: COLORS, scalar: 1.05, ticks: 220 };
	confetti({ ...common, origin: { x: 0, y: 0.95 }, angle: 60 });
	confetti({ ...common, origin: { x: 1, y: 0.95 }, angle: 120 });
}

/** A few firework-style bursts at random points over ~1.2s. */
export function fireworks(): void {
	if (!canCelebrate()) return;
	const end = Date.now() + 1200;
	const tick = () => {
		confetti({
			particleCount: 36,
			spread: 360,
			startVelocity: 28,
			gravity: 0.9,
			ticks: 200,
			colors: COLORS,
			origin: { x: 0.15 + Math.random() * 0.7, y: 0.2 + Math.random() * 0.4 },
		});
		if (Date.now() < end) setTimeout(tick, 260);
	};
	tick();
}

/** Stars raining down from the top. */
export function starShower(): void {
	if (!canCelebrate()) return;
	const shoot = (ratio: number, opts: confetti.Options) =>
		confetti({
			particleCount: Math.floor(46 * ratio),
			shapes: ['star'],
			colors: ['#f5b544', '#ffe08a', '#c6c0ff', '#7c6ff5'],
			ticks: 260,
			gravity: 0.7,
			...opts,
		});
	shoot(1, { spread: 80, startVelocity: 30, origin: { x: 0.5, y: 0 }, angle: 270 });
	shoot(0.7, { spread: 120, startVelocity: 22, origin: { x: 0.5, y: 0 }, angle: 270, scalar: 1.3 });
}

/** Balloons rising up the screen and gently swaying, then cleaned up. */
export function balloons(count = 14): void {
	if (!canCelebrate()) return;
	const layer = document.createElement('div');
	layer.setAttribute('aria-hidden', 'true');
	layer.style.cssText =
		'position:fixed;inset:0;z-index:2147483600;pointer-events:none;overflow:hidden;';
	document.body.appendChild(layer);

	let longest = 0;
	for (let i = 0; i < count; i++) {
		const color = COLORS[i % COLORS.length];
		const size = 30 + Math.random() * 26;
		const x = 4 + Math.random() * 92; // vw
		const duration = 3400 + Math.random() * 2200;
		const drift = (Math.random() - 0.5) * 90; // px sway
		const delay = Math.random() * 700;
		longest = Math.max(longest, duration + delay);

		const b = document.createElement('div');
		b.style.cssText = `position:absolute;left:${x}vw;bottom:-90px;width:${size}px;height:${size * 1.25}px;`;
		// Balloon body + string via a radial-gradient highlight; ::after-style string drawn with a child.
		const body = document.createElement('div');
		body.style.cssText = `width:100%;height:80%;border-radius:50% 50% 48% 48%;background:radial-gradient(circle at 32% 28%, #ffffffcc, ${color} 46%, ${color} 100%);box-shadow:0 6px 16px -6px ${color}aa;`;
		const string = document.createElement('div');
		string.style.cssText = `position:absolute;left:50%;top:78%;width:1.5px;height:30px;background:linear-gradient(${color},transparent);transform:translateX(-50%);`;
		b.appendChild(body);
		b.appendChild(string);
		layer.appendChild(b);

		b.animate(
			[
				{ transform: 'translate(0, 0) rotate(-3deg)', opacity: 1 },
				{ transform: `translate(${drift / 2}px, -55vh) rotate(3deg)`, opacity: 1, offset: 0.6 },
				{ transform: `translate(${drift}px, -112vh) rotate(-2deg)`, opacity: 0.85 },
			],
			{ duration, delay, easing: 'cubic-bezier(0.37, 0, 0.63, 1)', fill: 'forwards' },
		);
	}
	setTimeout(() => layer.remove(), longest + 400);
}

/** The big one when core setup is complete: confetti + balloons + a delayed second pop. */
export function bigFinale(): void {
	if (!canCelebrate()) return;
	confetti({ particleCount: 160, spread: 100, startVelocity: 45, origin: { y: 0.6 }, colors: COLORS, scalar: 1.1 });
	balloons(18);
	setTimeout(confettiBurst, 350);
	setTimeout(starShower, 850);
}

// Cycle through the milestone effects so consecutive steps feel different.
const MILESTONE_EFFECTS = [confettiBurst, balloons, fireworks, starShower];
let milestoneIndex = 0;

/** A per-milestone reward — varies each time it's called. */
export function celebrateMilestone(): void {
	if (!canCelebrate()) return;
	const effect = MILESTONE_EFFECTS[milestoneIndex % MILESTONE_EFFECTS.length];
	milestoneIndex += 1;
	effect();
}
