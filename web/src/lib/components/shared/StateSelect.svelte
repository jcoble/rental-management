<!--
  StateSelect — searchable US-state picker (drop-in for a text/select state field).

  Built on the shared Combobox (50+ items, so type-to-search matters). Binds the
  2-letter USPS code (uppercase, e.g. "CA"); empty string means "no state".

  Props:
    value       string  (bindable)  2-letter state code, or "" for empty. bind:value supported.
    onchange?   (code: string) => void   fired when the selection changes (gets the code, or "" if cleared).
    placeholder string  trigger/placeholder text. Default "Select state".
    disabled?   boolean
    includeTerritories? boolean  also list US territories (PR/GU/...). Default false.
    testid?     string  applied as data-testid on the input/trigger for E2E.
    id?         string  id for the input (label `for=` association).

  Value format: the bound value is always the 2-letter code. A drop-in replacement
  for `<input>`/`<select>` state fields — callers keep storing the same code string.
-->
<script lang="ts">
	import * as Combobox from '$lib/components/ui/combobox';
	import { US_STATES, US_TERRITORIES, type UsState } from '$lib/data/us-states';
	import { commitStateInput } from './resolve-state';

	let {
		value = $bindable(''),
		onchange,
		placeholder = 'Select state',
		disabled = false,
		includeTerritories = false,
		testid,
		id
	}: {
		value?: string;
		onchange?: (code: string) => void;
		placeholder?: string;
		disabled?: boolean;
		includeTerritories?: boolean;
		testid?: string;
		id?: string;
	} = $props();

	// Source list -> Combobox `items` shape ({ value, label }). Codes are uppercase.
	// Label includes the full name AND the code so searching either "Cal" or "CA"
	// finds California. The wrapper shows this label in the input when closed.
	const states = $derived<readonly UsState[]>(
		includeTerritories ? [...US_STATES, ...US_TERRITORIES] : US_STATES
	);
	const items = $derived(states.map((s) => ({ value: s.code, label: `${s.name} (${s.code})` })));

	// `inputValue` is the live text in the search box. The Combobox wrapper resets
	// it to the selected item's label when closed and clears it on open; while the
	// user types we read it here to filter the list ourselves.
	let inputValue = $state('');

	// Last text the user actually typed while the dropdown was open. We snapshot it
	// because on close the wrapper overwrites `inputValue` with the selected item's
	// label (empty when nothing was picked) BEFORE our onOpenChange handler runs —
	// so by then the typed text is gone. We resolve from this snapshot instead.
	let typedWhileOpen = $state('');
	let isOpen = $state(false);
	$effect(() => {
		if (isOpen) typedWhileOpen = inputValue;
	});

	// Escape means "cancel", not "commit": when the user hits Escape we skip resolving
	// the typed text on the close that follows, leaving the prior value untouched.
	let cancelNextClose = false;
	function handleKeydown(e: KeyboardEvent) {
		if (e.key === 'Escape') cancelNextClose = true;
	}

	// Whether the user explicitly picked an item (click / Enter) during this open
	// session. If so we must NOT re-resolve the typed text on close, because the
	// search box can still hold text that resolves to a *different* state than the
	// one they clicked (e.g. typed "kansas" — which substring-matches Arkansas too —
	// then clicked Arkansas). The explicit pick always wins.
	let pickedThisSession = false;

	const filtered = $derived(
		inputValue.trim() === ''
			? items
			: items.filter((i) => i.label.toLowerCase().includes(inputValue.trim().toLowerCase()))
	);

	function handleValueChange(next: string | undefined) {
		const code = (next ?? '').toUpperCase();
		pickedThisSession = true;
		value = code;
		onchange?.(code);
	}

	function commitTypedInput(typed: string) {
		const committed = commitStateInput({ typed, items, currentValue: value });
		if (committed.value !== value) {
			value = committed.value;
			onchange?.(committed.value);
		}
		inputValue = committed.label;
		typedWhileOpen = '';
	}

	// On close (Tab / blur / outside-click) the user may have typed a prefix without
	// explicitly picking an item. Resolve that typed text into a code and commit it —
	// and never blank an already-set value just because the typed text was unmatched.
	// Escape is the exception: it cancels, leaving the prior value as-is.
	function handleOpenChange(open: boolean) {
		isOpen = open;
		if (open) {
			// Fresh open session: nothing picked yet.
			pickedThisSession = false;
			return;
		}

		// Escape cancels, or the user already picked an item explicitly — either way,
		// don't re-resolve the typed text; just reflect the committed value's label.
		if (cancelNextClose || pickedThisSession) {
			cancelNextClose = false;
			typedWhileOpen = '';
			inputValue = items.find((i) => i.value === value)?.label ?? '';
			return;
		}

		commitTypedInput(typedWhileOpen);
	}

	function handleBlur() {
		if (isOpen || cancelNextClose || pickedThisSession) return;
		commitTypedInput(inputValue);
	}
</script>

<Combobox.Root
	type="single"
	{items}
	bind:value
	onValueChange={handleValueChange}
	bind:inputValue
	onOpenChange={handleOpenChange}
	{disabled}
>
	<Combobox.Input
		{placeholder}
		{id}
		data-testid={testid}
		aria-label={placeholder}
		onkeydown={handleKeydown}
		onblur={handleBlur}
	/>
	<Combobox.Content>
		{#each filtered as item (item.value)}
			<Combobox.Item value={item.value} label={item.label} />
		{/each}
		{#if filtered.length === 0}
			<Combobox.Empty>No state found.</Combobox.Empty>
		{/if}
	</Combobox.Content>
</Combobox.Root>
