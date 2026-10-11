<script lang="ts">
	type BrandMarkVariant = 'auto' | 'dark' | 'light';

	let {
		variant = 'auto',
		alt = '',
		class: className = ''
	}: {
		variant?: BrandMarkVariant;
		alt?: string;
		class?: string;
	} = $props();
</script>

<span
	class="brand-mark relative inline-block shrink-0 overflow-hidden rounded-[22%] align-middle {className}"
	role={alt ? 'img' : undefined}
	aria-label={alt || undefined}
	aria-hidden={alt ? undefined : 'true'}
>
	{#if variant === 'dark'}
		<img
			src="/brand/rental-command-icon-dark.png"
			alt=""
			class="h-full w-full object-cover"
			draggable="false"
		/>
	{:else if variant === 'light'}
		<img
			src="/brand/rental-command-icon-light.png"
			alt=""
			class="h-full w-full object-cover"
			draggable="false"
		/>
	{:else}
		<img
			src="/brand/rental-command-icon-dark.png"
			alt=""
			class="brand-mark-dark h-full w-full object-cover"
			draggable="false"
		/>
		<img
			src="/brand/rental-command-icon-light.png"
			alt=""
			class="brand-mark-light absolute inset-0 h-full w-full object-cover"
			draggable="false"
		/>
	{/if}
</span>

<style>
	.brand-mark-dark {
		display: block;
	}
	.brand-mark-light {
		display: none;
	}
	:global(.light) .brand-mark-dark,
	:global([data-theme='light']) .brand-mark-dark {
		display: none;
	}
	:global(.light) .brand-mark-light,
	:global([data-theme='light']) .brand-mark-light {
		display: block;
	}
</style>
