<script lang="ts">
	import * as Select from '$lib/components/ui/select';

	type Option = {
		value: string;
		label: string;
	};

	let {
		label,
		value = $bindable(''),
		display = '',
		editing = false,
		type = 'text',
		options = [],
		placeholder = '',
		error = '',
		testid,
		class: className = '',
		editTrigger = 'none',
		oneditrequest,
	}: {
		label: string;
		value?: string;
		display?: string | number | null | undefined;
		editing?: boolean;
		type?: 'text' | 'email' | 'tel' | 'number' | 'date' | 'datetime-local' | 'textarea' | 'select';
		options?: Option[];
		placeholder?: string;
		error?: string;
		testid: string;
		class?: string;
		/**
		 * How a display-mode value lets the user start editing.
		 *  - 'none'    (default, SAFE): the value is a calm, non-interactive row. The
		 *              only way into edit mode is the page's explicit Edit button.
		 *              A non-technical landlord can't flip the form by a stray click.
		 *  - 'confirm': the value is clickable, but clicking asks "Edit this record?"
		 *              via `oneditrequest` (the page opens a ConfirmDialog) — never
		 *              silently enters edit mode.
		 * The old behavior (a stray value-click silently flipping the whole form into
		 * edit mode) is intentionally gone — the primary user is a non-technical
		 * landlord who must not corrupt data by accident.
		 */
		editTrigger?: 'none' | 'confirm';
		/** Called when the user clicks the value with editTrigger='confirm'. */
		oneditrequest?: () => void;
	} = $props();

	const fieldId = $derived(`${testid}-input`);
	// View-mode text. When an explicit `display` is supplied, use it. Otherwise — for
	// select fields whose `value` IS present — resolve the human label from `options`
	// (so an enum/id-backed select never shows "-" in read mode just because the page
	// didn't pass a separate display string). Falls back to "-" only when there's
	// genuinely nothing to show.
	const displayText = $derived.by(() => {
		if (display != null && display !== '') return String(display);
		if (type === 'select' && value !== '' && value != null) {
			const match = options.find((o) => o.value === value);
			if (match) return match.label;
		}
		return '-';
	});
	const inputClass =
		'm3-field-surface h-11 w-full px-3 py-2 text-sm text-foreground outline-none';

	// Numeric fields are rendered as text inputs with a decimal input mode rather than
	// a native `type="number"`. This keeps the bound `value` a clean string (a native
	// number input lets Svelte 5 coerce `bind:value` to a `number`, which broke the
	// string-first Zod helpers — TSK-195) AND lets us reject non-numeric keystrokes up
	// front so garbage like "sdfds" can't be typed into a money box (TSK-198). Allowed
	// characters: digits, one decimal point, a leading minus, and editing keys.
	const isNumeric = $derived(type === 'number');
	function filterNumeric(e: Event) {
		const input = e.currentTarget as HTMLInputElement;
		// Strip anything that isn't a digit, dot or minus, collapse to one dot / leading minus.
		let cleaned = input.value.replace(/[^0-9.\-]/g, '');
		const negative = cleaned.startsWith('-');
		cleaned = cleaned.replace(/-/g, '');
		const firstDot = cleaned.indexOf('.');
		if (firstDot !== -1) {
			cleaned =
				cleaned.slice(0, firstDot + 1) + cleaned.slice(firstDot + 1).replace(/\./g, '');
		}
		if (negative) cleaned = '-' + cleaned;
		if (cleaned !== input.value) input.value = cleaned;
		value = cleaned;
	}
	// Label shown inside the Select trigger for the currently-bound value.
	const selectedLabel = $derived(
		options.find((o) => o.value === value)?.label ?? placeholder ?? 'Select…'
	);
</script>

<div class={className} data-testid={`${testid}-field`}>
	{#if editing}
		<label class="mb-1 block text-xs font-medium text-muted-foreground" for={fieldId}>{label}</label>
		{#if type === 'select'}
			<Select.Root type="single" bind:value>
				<Select.Trigger
					id={fieldId}
					data-testid={fieldId}
					class="m3-field-surface h-11 w-full bg-transparent"
					aria-label={label}
				>
					{selectedLabel}
				</Select.Trigger>
				<Select.Content>
					{#each options as option}
						<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>
					{/each}
				</Select.Content>
			</Select.Root>
		{:else if type === 'textarea'}
			<textarea
				id={fieldId}
				data-testid={fieldId}
				bind:value
				class="{inputClass} min-h-28 resize-y"
				{placeholder}
			></textarea>
		{:else if isNumeric}
			<input
				id={fieldId}
				data-testid={fieldId}
				{value}
				oninput={filterNumeric}
				class={inputClass}
				type="text"
				inputmode="decimal"
				{placeholder}
			/>
		{:else}
			<input id={fieldId} data-testid={fieldId} bind:value class={inputClass} {type} {placeholder} />
		{/if}
		{#if error}
			<p class="mt-1 text-xs text-destructive" data-testid={`${testid}-error`}>{error}</p>
		{/if}
	{:else}
		{#if editTrigger === 'confirm' && oneditrequest}
			<button
				type="button"
				class="m3-readonly-field m3-state-layer flex w-full flex-col justify-center text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/30"
				data-testid={`${testid}-value`}
				onclick={oneditrequest}
			>
				<span class="m3-readonly-field__label">{label}</span>
				<span class="m3-readonly-field__value mt-1">{displayText}</span>
			</button>
		{:else}
			<div class="m3-readonly-field flex flex-col justify-center" data-testid={`${testid}-value`}>
				<span class="m3-readonly-field__label">{label}</span>
				<span class="m3-readonly-field__value mt-1">{displayText}</span>
			</div>
		{/if}
	{/if}
</div>
