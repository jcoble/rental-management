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
		onedit,
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
		onedit?: () => void;
	} = $props();

	const fieldId = $derived(`${testid}-input`);
	const displayText = $derived(display == null || display === '' ? '-' : String(display));
	const inputClass =
		'h-10 w-full rounded-md border border-border bg-background px-3 py-2 text-sm text-foreground outline-none transition focus:border-ring focus:ring-2 focus:ring-ring/30';
	// Label shown inside the Select trigger for the currently-bound value.
	const selectedLabel = $derived(
		options.find((o) => o.value === value)?.label ?? placeholder ?? 'Select…'
	);
</script>

<div class={className} data-testid={`${testid}-field`}>
	<label class="mb-1 block text-xs font-medium text-muted-foreground" for={fieldId}>{label}</label>
	{#if editing}
		{#if type === 'select'}
			<Select.Root type="single" bind:value>
				<Select.Trigger
					id={fieldId}
					data-testid={fieldId}
					class="h-10 w-full bg-background"
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
		{:else}
			<input id={fieldId} data-testid={fieldId} bind:value class={inputClass} {type} {placeholder} />
		{/if}
		{#if error}
			<p class="mt-1 text-xs text-destructive" data-testid={`${testid}-error`}>{error}</p>
		{/if}
	{:else}
		{#if onedit}
			<button
				type="button"
				class="min-h-10 w-full rounded-md border border-transparent py-2 text-left text-sm font-medium text-foreground transition-colors hover:border-border hover:bg-muted/40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/30"
				data-testid={`${testid}-value`}
				onclick={onedit}
			>
				{displayText}
			</button>
		{:else}
			<p class="min-h-10 rounded-md border border-transparent py-2 text-sm font-medium text-foreground" data-testid={`${testid}-value`}>
				{displayText}
			</p>
		{/if}
	{/if}
</div>
