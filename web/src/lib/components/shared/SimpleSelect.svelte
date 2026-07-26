<script lang="ts">
	import * as Select from '$lib/components/ui/select';

	type Option = {
		value: string;
		label: string;
		disabled?: boolean;
	};

	let {
		value = $bindable(''),
		options,
		placeholder = 'Choose an option',
		disabled = false,
		testid,
		triggerClass = 'w-full',
		ariaLabel,
		onchange
	}: {
		value?: string;
		options: Option[];
		placeholder?: string;
		disabled?: boolean;
		testid?: string;
		triggerClass?: string;
		ariaLabel?: string;
		onchange?: (value: string) => void;
	} = $props();

	const selectedLabel = $derived(options.find((option) => option.value === value)?.label ?? placeholder);
	const EMPTY_VALUE = '__rental_command_empty_value__';
	const selectValue = $derived(value === '' ? EMPTY_VALUE : value);
</script>

<Select.Root
	type="single"
	value={selectValue}
	{disabled}
	onValueChange={(next) => {
		if (next === undefined) return;
		const nextValue = next === EMPTY_VALUE ? '' : next;
		value = nextValue;
		onchange?.(nextValue);
	}}
>
	<Select.Trigger class={triggerClass} data-testid={testid} aria-label={ariaLabel}>{selectedLabel}</Select.Trigger>
	<Select.Content>
		{#each options as option (option.value)}
			<Select.Item value={option.value === '' ? EMPTY_VALUE : option.value} label={option.label} disabled={option.disabled}>
				{option.label}
			</Select.Item>
		{/each}
	</Select.Content>
</Select.Root>
