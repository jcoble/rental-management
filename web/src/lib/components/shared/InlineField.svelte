<script lang="ts">
	import type { HTMLInputAttributes } from 'svelte/elements';
	import * as Select from '$lib/components/ui/select';
	import { maskInputValue, type InputMask } from '$lib/forms/input-masks';
	import DatePicker from './DatePicker.svelte';
	import DateTimePicker from './DateTimePicker.svelte';

	type Option = {
		value: string;
		label: string;
	};
	type InputMode = 'none' | 'text' | 'tel' | 'url' | 'email' | 'numeric' | 'decimal' | 'search';

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
		mask,
		inputmode,
		autocomplete,
		maxlength,
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
		mask?: InputMask;
		inputmode?: InputMode;
		autocomplete?: HTMLInputAttributes['autocomplete'];
		maxlength?: number;
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

	const isNumeric = $derived(type === 'number');
	function inferMask(): InputMask | undefined {
		if (mask) return mask;
		const key = label.toLowerCase();
		if (type === 'tel') return 'phone';
		if (key.includes('zip')) return 'zip';
		if (!isNumeric) return undefined;
		if (key.includes('rate') || key.includes('%')) return 'percentage';
		if (key.includes('day') || key.includes('month') || key.includes('term')) return 'integer';
		return 'currency';
	}
	const activeMask = $derived(inferMask());
	const activeInputMode = $derived(
		inputmode ??
			(activeMask === 'phone'
				? 'tel'
				: activeMask === 'integer' || activeMask === 'zip' || activeMask === 'cardLast4'
					? 'numeric'
					: activeMask === 'currency' || activeMask === 'decimal' || activeMask === 'percentage'
						? 'decimal'
						: undefined)
	);
	const activeAutocomplete = $derived(
		autocomplete ??
			(type === 'email'
				? 'email'
				: type === 'tel'
					? 'tel'
					: activeMask === 'zip'
						? 'postal-code'
						: undefined)
	);
	const activeMaxLength = $derived(
		maxlength ?? (activeMask === 'zip' ? 10 : activeMask === 'cardLast4' ? 4 : undefined)
	);
	function handleMaskedInput(e: Event) {
		if (!activeMask) return;
		const input = e.currentTarget as HTMLInputElement;
		const cleaned = maskInputValue(input.value, activeMask, { maxLength: activeMaxLength });
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
		{:else if type === 'date'}
			<DatePicker id={fieldId} testid={fieldId} bind:value {placeholder} />
		{:else if type === 'datetime-local'}
			<DateTimePicker id={fieldId} testid={fieldId} bind:value />
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
				oninput={handleMaskedInput}
				class={inputClass}
				type="text"
				inputmode={activeInputMode}
				autocomplete={activeAutocomplete}
				maxlength={activeMaxLength}
				{placeholder}
			/>
		{:else}
			<input
				id={fieldId}
				data-testid={fieldId}
				bind:value
				class={inputClass}
				{type}
				{placeholder}
				inputmode={activeInputMode}
				autocomplete={activeAutocomplete}
				maxlength={activeMaxLength}
				oninput={handleMaskedInput}
			/>
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
