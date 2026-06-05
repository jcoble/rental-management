<!--
  AddressAutocomplete — street-address input with optional Google Places autocomplete.

  Manual entry always works. When the API has a Google Places key configured
  (the "GooglePlaces:ApiKey" config section — user-secrets in dev, container env in
  prod — read by RentalCommand.Api's PlacesController/GooglePlacesService), typing
  shows a suggestion dropdown; picking one fills the street line and (when the parent
  provides `onresolved`) autofills city/state/zip.

  SELF-GATING: the component calls the same-origin `/api/v1/places/autocomplete`
  endpoint, which returns `enabled:false` when no key is configured. On the first such
  response (or any network error) the feature disables itself process-wide and the
  field behaves as a plain text input. So the manual-entry fallback can never break,
  with or without a key.

  Props:
    value       string  (bindable)  the street line (line1). bind:value supported.
    onchange?   (line1) => void              fired on every manual input change.
    onresolved? (addr {line1,city,state,zip}) => void
                fired ONLY when a suggestion is picked, so the parent can autofill
                the rest of the address. Never fires during manual typing.
    enabled?    boolean  set false to force manual-only. Default true (auto-detect).
    placeholder string  Default "Street address".
    disabled?   boolean
    testid?     string  data-testid on the input.
    id?         string  id for label association.
-->
<script lang="ts">
	import { tick } from 'svelte';

	let {
		value = $bindable(''),
		onchange,
		onresolved,
		enabled = true,
		placeholder = 'Street address',
		disabled = false,
		testid,
		id
	}: {
		value?: string;
		onchange?: (line1: string) => void;
		onresolved?: (addr: { line1: string; city: string; state: string; zip: string }) => void;
		enabled?: boolean;
		placeholder?: string;
		disabled?: boolean;
		testid?: string;
		id?: string;
	} = $props();

	interface Suggestion {
		placeId: string;
		primary: string;
		secondary: string;
	}

	// Process-wide gate: once the server tells us there's no key (or a call errors),
	// every instance stops calling and stays a plain input for the rest of the session.
	let placesDisabled = $state(false);

	let suggestions = $state<Suggestion[]>([]);
	let open = $state(false);
	let activeIndex = $state(-1);
	let sessionToken = newToken();
	let debounceTimer: ReturnType<typeof setTimeout> | undefined;
	let seq = 0; // guards against out-of-order responses

	const listboxId = `addr-listbox-${Math.random().toString(36).slice(2, 8)}`;
	const canAutocomplete = $derived(enabled && !placesDisabled && !disabled);

	const inputClass =
		'h-10 w-full rounded-md border border-input bg-transparent px-3 py-2 text-sm text-foreground outline-none transition-[color,box-shadow] placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-ring/50 focus-visible:ring-[3px] disabled:cursor-not-allowed disabled:opacity-50';

	function newToken(): string {
		try {
			return crypto.randomUUID();
		} catch {
			return `${Date.now()}-${Math.random().toString(36).slice(2)}`;
		}
	}

	function closeDropdown() {
		open = false;
		activeIndex = -1;
	}

	function handleInput(e: Event) {
		value = (e.target as HTMLInputElement).value;
		onchange?.(value);
		if (!canAutocomplete) return;
		scheduleFetch(value);
	}

	function scheduleFetch(q: string) {
		clearTimeout(debounceTimer);
		if (q.trim().length < 3) {
			closeDropdown();
			suggestions = [];
			return;
		}
		debounceTimer = setTimeout(() => fetchSuggestions(q), 250);
	}

	async function fetchSuggestions(q: string) {
		const mySeq = ++seq;
		try {
			const res = await fetch(
				`/api/v1/places/autocomplete?q=${encodeURIComponent(q)}&session=${sessionToken}`
			);
			if (!res.ok) {
				placesDisabled = true;
				closeDropdown();
				return;
			}
			const data = (await res.json()) as { enabled: boolean; suggestions: Suggestion[] };
			if (!data.enabled) {
				placesDisabled = true; // no key configured → never call again this session
				closeDropdown();
				return;
			}
			if (mySeq !== seq) return; // a newer keystroke superseded this response
			suggestions = data.suggestions ?? [];
			activeIndex = -1;
			open = suggestions.length > 0;
		} catch {
			// Network hiccup: fall back to manual entry silently (don't hard-disable).
			closeDropdown();
		}
	}

	async function pick(s: Suggestion) {
		// Fill the street line immediately from the suggestion's primary text.
		value = s.primary || value;
		onchange?.(value);
		closeDropdown();

		try {
			const res = await fetch(
				`/api/v1/places/details?placeId=${encodeURIComponent(s.placeId)}&session=${sessionToken}`
			);
			if (res.ok) {
				const addr = (await res.json()) as {
					line1: string;
					city: string;
					state: string;
					zip: string;
				};
				if (addr.line1) {
					value = addr.line1;
					onchange?.(value);
				}
				onresolved?.(addr);
			}
		} catch {
			// Details lookup failed — keep the street line we already set.
		} finally {
			// A details call closes the billing session; start a fresh one.
			sessionToken = newToken();
		}
	}

	async function onKeydown(e: KeyboardEvent) {
		if (!open || suggestions.length === 0) return;
		if (e.key === 'ArrowDown') {
			e.preventDefault();
			activeIndex = (activeIndex + 1) % suggestions.length;
		} else if (e.key === 'ArrowUp') {
			e.preventDefault();
			activeIndex = activeIndex <= 0 ? suggestions.length - 1 : activeIndex - 1;
		} else if (e.key === 'Enter') {
			if (activeIndex >= 0) {
				e.preventDefault();
				await pick(suggestions[activeIndex]);
			}
		} else if (e.key === 'Escape') {
			closeDropdown();
		}
		await tick();
	}

	function onFocusOut(e: FocusEvent) {
		// Close only when focus leaves the whole component (so clicking an item works).
		const next = e.relatedTarget as Node | null;
		if (next && (e.currentTarget as HTMLElement).contains(next)) return;
		closeDropdown();
	}
</script>

<div class="relative" onfocusout={onFocusOut}>
	<input
		type="text"
		class={inputClass}
		{value}
		oninput={handleInput}
		onkeydown={onKeydown}
		{placeholder}
		{disabled}
		{id}
		data-testid={testid}
		autocomplete={canAutocomplete ? 'off' : 'street-address'}
		aria-label={placeholder}
		role="combobox"
		aria-expanded={open}
		aria-controls={listboxId}
		aria-autocomplete="list"
		aria-activedescendant={activeIndex >= 0 ? `${listboxId}-${activeIndex}` : undefined}
	/>

	{#if open && suggestions.length > 0}
		<ul
			id={listboxId}
			role="listbox"
			class="absolute z-50 mt-1 max-h-72 w-full overflow-auto rounded-md border border-border bg-popover py-1 text-sm shadow-lg"
			data-testid={testid ? `${testid}-suggestions` : 'address-suggestions'}
		>
			{#each suggestions as s, i (s.placeId)}
				<li role="presentation">
					<button
						type="button"
						id="{listboxId}-{i}"
						role="option"
						aria-selected={i === activeIndex}
						class="flex w-full flex-col items-start gap-0.5 px-3 py-2 text-left transition-colors {i ===
						activeIndex
							? 'bg-accent text-accent-foreground'
							: 'hover:bg-accent/60'}"
						onpointerdown={(e) => {
							// pointerdown (not click) so it beats the input's focusout/blur.
							e.preventDefault();
							pick(s);
						}}
						onmousemove={() => (activeIndex = i)}
					>
						<span class="font-medium text-foreground">{s.primary}</span>
						{#if s.secondary}
							<span class="text-xs text-muted-foreground">{s.secondary}</span>
						{/if}
					</button>
				</li>
			{/each}
		</ul>
	{/if}
</div>
