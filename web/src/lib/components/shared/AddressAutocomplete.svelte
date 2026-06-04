<!--
  AddressAutocomplete — street-address input with optional, GATED autocomplete.

  DEFAULT (and only currently-shippable) behavior is a clean, fully-functional
  plain text input for manual entry. There is NO address API key configured in the
  project yet, so autocomplete is OFF by default.

  GATING (mirrors the Google-auth pattern, e.g. `login/+page.server.ts` deriving
  `googleEnabled` from `PUBLIC_GOOGLE_CLIENT_ID`): this component takes an `enabled`
  prop (default false). To turn autocomplete on later, a parent computes
  availability server-side from a public env flag and passes `enabled` down, e.g.:

      // +page.server.ts
      return { addressAutocompleteEnabled: Boolean(env.PUBLIC_ADDRESS_AUTOCOMPLETE_KEY) };

  Then `<AddressAutocomplete enabled={data.addressAutocompleteEnabled} ... />`.

  TODO (integration point — do NOT call any external API until a key is wired):
    When `enabled`, debounce `value` input and fetch suggestions from the chosen
    provider (Google Places Autocomplete or Mapbox — see TODO.md #19 / #372).
    Render a suggestion dropdown; on pick, set `value` to the street line and call
    `onresolved({ line1, city, state, zip })` so the parent autofills city/state/
    zip. Suggestion fetching MUST stay gated behind `enabled` so the manual-entry
    fallback is never broken when no key exists.

  Props:
    value       string  (bindable)  the street address line (line1). bind:value supported.
    onchange?   (line1: string) => void   fired on every input change.
    onresolved? (addr: { line1: string; city: string; state: string; zip: string }) => void
                fired ONLY when a suggestion is picked (autocomplete mode), so the
                parent can autofill the rest of the address. Never fires in manual mode.
    enabled?    boolean  turn on the (future) suggestion dropdown. Default false.
    placeholder string  Default "Street address".
    disabled?   boolean
    testid?     string  applied as data-testid on the input for E2E.
    id?         string  id for the input (label `for=` association).

  Value format: `value` is just the street line (e.g. "123 Main St, Apt 4"); city/
  state/zip are separate fields the parent owns and may receive via `onresolved`.
-->
<script lang="ts">
	let {
		value = $bindable(''),
		onchange,
		// eslint-disable-next-line @typescript-eslint/no-unused-vars
		onresolved,
		enabled = false,
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

	// Whether the suggestion dropdown is active. Today this is always false (no key
	// configured); structured so a provider can flip it on via the `enabled` prop +
	// the TODO integration above. `onresolved` is referenced here so the prop stays
	// part of the stable API even while the dropdown is unimplemented.
	const autocompleteActive = $derived(enabled && typeof onresolved === 'function');

	const inputClass =
		'h-10 w-full rounded-md border border-input bg-transparent px-3 py-2 text-sm text-foreground outline-none transition-[color,box-shadow] placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-ring/50 focus-visible:ring-[3px] disabled:cursor-not-allowed disabled:opacity-50';

	function handleInput(e: Event) {
		value = (e.target as HTMLInputElement).value;
		onchange?.(value);
		// When autocompleteActive, a future provider would debounce + fetch here.
	}
</script>

<div class="relative">
	<input
		type="text"
		class={inputClass}
		{value}
		oninput={handleInput}
		{placeholder}
		{disabled}
		{id}
		data-testid={testid}
		autocomplete={autocompleteActive ? 'off' : 'street-address'}
		aria-label={placeholder}
	/>
	<!--
	  Suggestion dropdown renders here when a provider is wired (gated on
	  `autocompleteActive`). Intentionally empty until an address API key exists.
	-->
</div>
