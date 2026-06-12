<!--
  TimeZoneSelect — searchable IANA time-zone picker (drop-in for a text time-zone field).

  Built on the shared Combobox (hundreds of items, so type-to-search matters), mirroring
  StateSelect. Binds the canonical IANA id (e.g. "America/New_York"); empty string means
  "no zone selected". Common US zones are surfaced at the top with friendly labels; the
  full IANA list follows.

  Props:
    value       string  (bindable)  IANA id, or "" for empty. bind:value supported.
    onchange?   (id: string) => void   fired when the selection changes.
    placeholder string  trigger/placeholder text. Default "Select time zone".
    disabled?   boolean
    testid?     string  applied as data-testid on the input/trigger for E2E.
    id?         string  id for the input (label `for=` association).

  Value format: the bound value is always the canonical IANA id — a drop-in replacement
  for the old free-form `<input>` (callers keep storing the same string).
-->
<script lang="ts">
	import * as Combobox from '$lib/components/ui/combobox';
	import { timeZoneItems, type TimeZoneOption } from '$lib/data/timezones';

	let {
		value = $bindable(''),
		onchange,
		placeholder = 'Select time zone',
		disabled = false,
		testid,
		id
	}: {
		value?: string;
		onchange?: (id: string) => void;
		placeholder?: string;
		disabled?: boolean;
		testid?: string;
		id?: string;
	} = $props();

	// Built once from the runtime's supported zones (common US zones first).
	const items: TimeZoneOption[] = timeZoneItems();

	// Live text in the search box. The Combobox wrapper resets it to the selected
	// item's label on close and clears it on open; while open we filter on it.
	let inputValue = $state('');

	// Snapshot of what the user typed while the dropdown was open — see StateSelect
	// for the rationale (the wrapper overwrites inputValue on close before our
	// onOpenChange runs, so we resolve from this snapshot instead).
	let typedWhileOpen = $state('');
	let isOpen = $state(false);
	$effect(() => {
		if (isOpen) typedWhileOpen = inputValue;
	});

	// Escape cancels (keep prior value); an explicit pick wins over typed text.
	let cancelNextClose = false;
	let pickedThisSession = false;
	function handleKeydown(e: KeyboardEvent) {
		if (e.key === 'Escape') cancelNextClose = true;
	}

	const filtered = $derived(
		inputValue.trim() === ''
			? items
			: items.filter((i) => i.label.toLowerCase().includes(inputValue.trim().toLowerCase()))
	);

	function labelFor(v: string): string {
		return items.find((i) => i.value === v)?.label ?? v;
	}

	function handleValueChange(next: string | undefined) {
		const id = next ?? '';
		pickedThisSession = true;
		value = id;
		onchange?.(id);
	}

	// On close without an explicit pick, resolve typed text into an IANA id (exact id,
	// exact label, then unique prefix). Never blank an already-set value on gibberish.
	function resolveTyped(typed: string): string {
		const text = typed.trim();
		if (text === '') return value;
		const lower = text.toLowerCase();

		const byId = items.find((i) => i.value.toLowerCase() === lower);
		if (byId) return byId.value;

		const byLabel = items.find((i) => i.label.toLowerCase() === lower);
		if (byLabel) return byLabel.value;

		const prefixMatches = items.filter(
			(i) => i.value.toLowerCase().startsWith(lower) || i.label.toLowerCase().startsWith(lower)
		);
		if (prefixMatches.length === 1) return prefixMatches[0].value;

		return value;
	}

	function handleOpenChange(open: boolean) {
		isOpen = open;
		if (open) {
			pickedThisSession = false;
			return;
		}
		if (cancelNextClose || pickedThisSession) {
			cancelNextClose = false;
			typedWhileOpen = '';
			inputValue = labelFor(value);
			return;
		}
		const resolved = resolveTyped(typedWhileOpen);
		if (resolved !== value) {
			value = resolved;
			onchange?.(resolved);
		}
		inputValue = labelFor(resolved);
		typedWhileOpen = '';
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
	/>
	<Combobox.Content>
		{#each filtered as item (item.value)}
			<Combobox.Item value={item.value} label={item.label} />
		{/each}
		{#if filtered.length === 0}
			<Combobox.Empty>No time zone found.</Combobox.Empty>
		{/if}
	</Combobox.Content>
</Combobox.Root>
