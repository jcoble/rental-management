<!--
  CoachTrigger — watches the URL for a `?coach=<key>` deep-link and fires the spotlight on the matching
  `data-coach` element, then strips the param (so a refresh / back-nav doesn't re-trigger). Mounted
  ONCE globally next to CoachOverlay; works for any page reached via a checklist deep-link, so target
  pages need no per-page wiring beyond their `data-coach` attribute.
-->
<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { browser } from '$app/environment';
	import { startCoach } from '$lib/onboarding/coach.svelte';
	import { coachCaption } from '$lib/onboarding/coach-captions';

	// Remember the last key we acted on so we don't re-fire while the param is being cleared, but DO
	// fire again if the user follows another coach deep-link to the same page later.
	let lastHandled = $state<string | null>(null);

	$effect(() => {
		if (!browser) return;
		const key = page.url.searchParams.get('coach');
		if (!key) {
			lastHandled = null;
			return;
		}
		if (key === lastHandled) return;
		lastHandled = key;

		// Strip ?coach from the URL (keep the rest, incl. any #hash) via an in-place replace, so the
		// spotlight is a one-shot and the address bar stays clean. noScroll/keepFocus so removing the
		// param doesn't disturb the page the user just landed on.
		const url = new URL(page.url);
		url.searchParams.delete('coach');
		void goto(url.pathname + url.search + url.hash, {
			replaceState: true,
			noScroll: true,
			keepFocus: true,
		});

		void startCoach(key, coachCaption(key));
	});
</script>
