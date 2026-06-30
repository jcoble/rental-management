<script lang="ts">
	import { ArrowRight, Check } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';

	let {
		complete = false,
		disabled = false,
		testid = 'step-next',
		onclick
	}: {
		complete?: boolean;
		disabled?: boolean;
		testid?: string;
		onclick?: (event: MouseEvent) => void;
	} = $props();
</script>

<Button
	data-testid={testid}
	{disabled}
	{onclick}
	class="gap-2 overflow-hidden transition-all duration-200 {complete
		? 'bg-success text-success-foreground hover:bg-success/90'
		: ''}"
>
	<span class="relative grid h-4 w-4 place-items-center" aria-hidden="true">
		<ArrowRight
			class="absolute h-4 w-4 transition-all duration-200 {complete
				? 'scale-50 rotate-90 opacity-0'
				: 'scale-100 rotate-0 opacity-100'}"
		/>
		<Check
			class="absolute h-4 w-4 transition-all duration-200 {complete
				? 'scale-100 rotate-0 opacity-100'
				: 'scale-50 -rotate-90 opacity-0'}"
		/>
	</span>
	<span>{complete ? 'Done' : 'Next'}</span>
</Button>
