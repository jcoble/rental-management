<script lang="ts">
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
	} = $props();

	const fieldId = $derived(`${testid}-input`);
	const displayText = $derived(display == null || display === '' ? '-' : String(display));
	const inputClass =
		'h-10 w-full rounded-md border border-border bg-background px-3 py-2 text-sm text-foreground outline-none transition focus:border-ring focus:ring-2 focus:ring-ring/30';
</script>

<div class={className} data-testid={`${testid}-field`}>
	<label class="mb-1 block text-xs font-medium text-muted-foreground" for={fieldId}>{label}</label>
	{#if editing}
		{#if type === 'select'}
			<select id={fieldId} data-testid={fieldId} bind:value class={inputClass}>
				{#each options as option}
					<option value={option.value}>{option.label}</option>
				{/each}
			</select>
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
		<p class="min-h-10 rounded-md border border-transparent py-2 text-sm font-medium text-foreground" data-testid={`${testid}-value`}>
			{displayText}
		</p>
	{/if}
</div>
