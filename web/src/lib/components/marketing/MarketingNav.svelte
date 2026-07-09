<script lang="ts">
	import { onMount } from 'svelte';
	import { ArrowRight, Menu, X } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import BrandMark from '$lib/components/BrandMark.svelte';
	import { onScroll } from '$lib/scroll/smooth';

	/**
	 * Shared sticky marketing header (landing + features + any public page that opts
	 * in). Subscribes to the smooth-scroll engine for its background state + the
	 * scroll-progress rail, and ships a real mobile menu so the Features/Docs links
	 * aren't lost below `md`. `current` highlights the active top-level link.
	 */
	let { current }: { current?: 'features' | 'docs' } = $props();

	let scrolled = $state(false);
	let progress = $state(0);
	let open = $state(false);

	onMount(() =>
		onScroll((scroll, prog) => {
			scrolled = scroll > 8;
			progress = prog;
			if (open && scroll > 8) open = false; // close the menu once they start scrolling
		})
	);

	const links = [
		{ href: '/features', label: 'Features', key: 'features' },
		{ href: '/docs', label: 'Docs', key: 'docs' }
	] as const;
</script>

<header
	class="sticky top-0 z-50 transition-[background-color,border-color,box-shadow] duration-300 {scrolled || open
		? 'border-b border-border/70 bg-background/90 shadow-[0_8px_30px_-12px_rgba(0,0,0,0.5)] backdrop-blur-xl'
		: 'border-b border-transparent bg-background/30 backdrop-blur-md'}"
>
	<div class="mx-auto flex h-16 max-w-6xl items-center justify-between px-5 sm:px-8">
		<a href="/welcome" class="group flex items-center gap-2.5">
			<BrandMark class="h-8 w-8 shadow-sm transition-transform duration-300 group-hover:scale-105" />
			<span class="text-base font-semibold tracking-tight">Rental Command</span>
		</a>

		<nav class="hidden items-center gap-1 md:flex">
			{#each links as l (l.key)}
				<a
					href={l.href}
					class="rounded-full px-3 py-1.5 text-sm transition-colors hover:bg-foreground/5 {current === l.key
						? 'font-medium text-foreground'
						: 'text-muted-foreground hover:text-foreground'}"
				>
					{l.label}
				</a>
			{/each}
		</nav>

		<div class="flex items-center gap-2">
			<Button href="/login" variant="ghost" size="sm" class="hidden sm:inline-flex">Sign in</Button>
			<Button href="/register" size="sm">
				Get started
				<ArrowRight class="h-4 w-4" />
			</Button>
			<button
				type="button"
				class="inline-flex h-9 w-9 items-center justify-center rounded-full text-muted-foreground transition-colors hover:bg-foreground/5 hover:text-foreground md:hidden"
				aria-label={open ? 'Close menu' : 'Open menu'}
				aria-expanded={open}
				onclick={() => (open = !open)}
			>
				{#if open}<X class="h-5 w-5" />{:else}<Menu class="h-5 w-5" />{/if}
			</button>
		</div>
	</div>

	{#if open}
		<div class="border-t border-border/60 md:hidden">
			<nav class="mx-auto flex max-w-6xl flex-col px-4 py-2 sm:px-6">
				{#each links as l (l.key)}
					<a
						href={l.href}
						onclick={() => (open = false)}
						class="rounded-lg px-3 py-2.5 text-sm font-medium {current === l.key
							? 'text-foreground'
							: 'text-muted-foreground'} transition-colors hover:bg-foreground/5 hover:text-foreground"
					>
						{l.label}
					</a>
				{/each}
				<a
					href="/login"
					onclick={() => (open = false)}
					class="rounded-lg px-3 py-2.5 text-sm font-medium text-muted-foreground transition-colors hover:bg-foreground/5 hover:text-foreground sm:hidden"
				>
					Sign in
				</a>
				<a
					href="/register"
					onclick={() => (open = false)}
					class="mt-1 inline-flex items-center gap-1.5 rounded-lg px-3 py-2.5 text-sm font-semibold text-primary transition-colors hover:bg-primary/10"
				>
					Get started
					<ArrowRight class="h-4 w-4" />
				</a>
			</nav>
		</div>
	{/if}

	<div
		class="nav-progress absolute inset-x-0 bottom-0 h-px origin-left bg-gradient-to-r from-primary via-chart-4 to-primary"
		style="transform: scaleX({progress})"
		aria-hidden="true"
	></div>
</header>

<style>
	.nav-progress {
		transform: scaleX(0);
	}
</style>
