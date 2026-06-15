<script lang="ts">
	import { page } from '$app/state';
	import { Activity, ArrowLeft, ShieldAlert } from '@lucide/svelte';
	import NavigationLoader from '$lib/components/NavigationLoader.svelte';
	import ThemeModeToggle from '$lib/components/shared/ThemeModeToggle.svelte';

	let { children }: { children: import('svelte').Snippet } = $props();

	// Minimal operator nav — deliberately separate from the landlord AppShell so platform
	// plumbing never bleeds into the customer sidebar (EdiPlatform AdminNav pattern, F6).
	const navItems = [{ href: '/superadmin/engine', label: 'Engine Health', icon: Activity }];

	function isActive(href: string): boolean {
		return page.url.pathname.startsWith(href);
	}
</script>

<NavigationLoader />

<div class="flex h-full w-full overflow-hidden bg-background">
	<aside class="flex h-full w-56 shrink-0 flex-col border-r border-sidebar-border bg-sidebar/95 backdrop-blur">
		<div class="flex h-14 items-center gap-2 border-b border-sidebar-border px-4">
			<span class="flex h-7 w-7 items-center justify-center rounded-[var(--m3-shape-large)] bg-destructive/15 text-destructive ring-1 ring-inset ring-destructive/25">
				<ShieldAlert class="h-4 w-4" />
			</span>
			<span class="truncate text-sm font-semibold tracking-tight text-foreground">Platform Ops</span>
		</div>

		<nav class="flex-1 overflow-y-auto px-2 py-3" data-testid="superadmin-nav">
			{#each navItems as item}
				{@const active = isActive(item.href)}
				<a
					href={item.href}
					class="m3-state-layer flex items-center gap-2 rounded-[var(--m3-shape-full)] px-3 py-2 text-sm transition-colors
						{active
						? 'bg-sidebar-accent font-medium text-sidebar-accent-foreground shadow-[inset_0_0_0_1px_color-mix(in_srgb,var(--primary)_24%,transparent)]'
						: 'text-muted-foreground hover:bg-sidebar-accent hover:text-sidebar-foreground'}"
					data-testid="superadmin-nav-{item.href.split('/').pop()}"
				>
					<item.icon class="h-4 w-4 shrink-0 {active ? 'text-primary' : ''}" />
					<span class="truncate">{item.label}</span>
				</a>
			{/each}
		</nav>

		<div class="border-t border-sidebar-border p-2">
			<a
				href="/"
				class="m3-state-layer flex items-center gap-2 rounded-[var(--m3-shape-full)] px-3 py-2 text-sm text-muted-foreground transition-colors hover:bg-sidebar-accent hover:text-sidebar-foreground"
				data-testid="superadmin-back-to-app"
			>
				<ArrowLeft class="h-4 w-4 shrink-0" />
				<span>Back to app</span>
			</a>
		</div>
	</aside>

	<div class="flex h-full min-w-0 flex-1 flex-col">
		<header class="sticky top-0 z-30 flex h-14 items-center gap-2 border-b border-border bg-background/88 px-4 backdrop-blur">
			<span class="truncate text-sm font-semibold tracking-tight text-foreground" data-testid="superadmin-page-title">
				Platform Operations
			</span>
			<div class="ml-auto flex items-center gap-1">
				<ThemeModeToggle data-testid="superadmin-theme-toggle" />
			</div>
		</header>

		<main class="flex min-h-0 flex-1 justify-center overflow-hidden">
			<div class="h-full w-full max-w-[1600px]">
				{@render children()}
			</div>
		</main>
	</div>
</div>
