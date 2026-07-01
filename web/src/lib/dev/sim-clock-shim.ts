/**
 * Inline `Date` shim for the master simulation clock (dev-only, spec §7.1).
 *
 * Exported as a STRING so `hooks.server.ts` can inline it verbatim into `<head>` (via
 * `transformPageChunk`, replacing the `<!--%sim-clock-shim%-->` placeholder) — ONLY when
 * `PUBLIC_SIMULATION_ENABLED === 'true'`. Inlining (rather than a module import) guarantees `Date` is
 * patched BEFORE any SvelteKit/app code runs.
 *
 * What it does:
 *  - captures the real constructor as `window.__RealDate` (so auth-expiry math can stay on real time —
 *    see `$lib/dev/real-time`), and exposes `window.__simClock` for the client sync bootstrap;
 *  - virtualizes ONLY no-arg `new Date()` and `Date.now()` for DISPLAY, driven by the `rc_sim` cookie
 *    (`{ offsetMs, mode, anchorMs }`) so a warm reload is synchronously correct;
 *  - passes every other constructor (`new Date(...args)`), `Date.parse`, and `Date.UTC` straight
 *    through to the real `Date`, and preserves `instanceof` by sharing the prototype.
 *
 * Self-contained (no imports) and wrapped in try/catch so a shim failure can never break page boot.
 */
export const SIM_CLOCK_SHIM_PLACEHOLDER = '<!--%sim-clock-shim%-->';

export const simClockShimScript = `<script>
(function () {
	try {
		var RealDate = Date;
		window.__RealDate = RealDate;

		var mode = 'real';
		var offsetMs = 0;
		var anchorMs = 0;

		// Warm-reload seed: read the rc_sim cookie (no regex → no template-literal escaping pitfalls).
		var parts = document.cookie ? document.cookie.split(';') : [];
		for (var i = 0; i < parts.length; i++) {
			var p = parts[i].trim();
			if (p.indexOf('rc_sim=') === 0) {
				try {
					var c = JSON.parse(decodeURIComponent(p.substring(7)));
					if (c) {
						if (typeof c.offsetMs === 'number') offsetMs = c.offsetMs;
						if (typeof c.anchorMs === 'number') anchorMs = c.anchorMs;
						if (typeof c.mode === 'string') mode = c.mode;
					}
				} catch (e) { /* malformed cookie → defaults (real time) */ }
				break;
			}
		}

		function nowMs() {
			return mode === 'frozen' ? anchorMs : RealDate.now() + offsetMs;
		}

		// Only no-arg construction is virtualized; every other arity delegates to the real Date.
		function SimDate() {
			if (arguments.length === 0) return new RealDate(nowMs());
			switch (arguments.length) {
				case 1: return new RealDate(arguments[0]);
				case 2: return new RealDate(arguments[0], arguments[1]);
				case 3: return new RealDate(arguments[0], arguments[1], arguments[2]);
				case 4: return new RealDate(arguments[0], arguments[1], arguments[2], arguments[3]);
				case 5: return new RealDate(arguments[0], arguments[1], arguments[2], arguments[3], arguments[4]);
				case 6: return new RealDate(arguments[0], arguments[1], arguments[2], arguments[3], arguments[4], arguments[5]);
				default: return new RealDate(arguments[0], arguments[1], arguments[2], arguments[3], arguments[4], arguments[5], arguments[6]);
			}
		}

		SimDate.prototype = RealDate.prototype;   // instanceof Date + all Date methods keep working
		SimDate.now = function () { return nowMs(); };
		SimDate.parse = RealDate.parse;
		SimDate.UTC = RealDate.UTC;

		window.Date = SimDate;

		window.__simClock = {
			realNow: function () { return RealDate.now(); },
			getState: function () { return { mode: mode, offsetMs: offsetMs, anchorMs: anchorMs }; },
			setOffset: function (nextOffsetMs, nextMode, nextAnchorMs) {
				offsetMs = typeof nextOffsetMs === 'number' ? nextOffsetMs : 0;
				mode = nextMode || 'real';
				anchorMs = typeof nextAnchorMs === 'number' ? nextAnchorMs : 0;
			}
		};
	} catch (e) {
		if (window.console && console.warn) console.warn('sim-clock shim failed', e);
	}
})();
</script>`;
