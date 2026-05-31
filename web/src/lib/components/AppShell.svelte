<script lang="ts">
	import { page } from '$app/stores';
	import {
		LayoutDashboard,
		Building,
		Users,
		FileText,
		Wrench,
		Calculator,
		Calendar,
		BadgeDollarSign,
		Sparkles,
		Settings,
		PanelLeftClose,
		PanelLeftOpen,
		Shield,
		UserCircle2,
		LogOut
	} from '@lucide/svelte';
	import PortfolioSelector from '$lib/components/shared/PortfolioSelector.svelte';
	import { getCurrentUser, hasAnyRole } from '$lib/stores/auth.svelte';

	let { children }: { children: import('svelte').Snippet } = $props();

	let sidebarCollapsed = $state(false);
	let isMobile = $state(false);

	const navItems: { href: string; label: string; icon: typeof Building; roles?: string[] }[] = [
		{ href: '/', label: 'Dashboard', icon: LayoutDashboard },
		{ href: '/properties', label: 'Properties', icon: Building, roles: ['Admin', 'Manager', 'Agent'] },
		{ href: '/tenants', label: 'Tenants', icon: Users, roles: ['Admin', 'Manager', 'Agent'] },
		{ href: '/leases', label: 'Leases', icon: FileText, roles: ['Admin', 'Manager', 'Agent'] },
		{ href: '/maintenance', label: 'Maintenance', icon: Wrench, roles: ['Admin', 'Manager', 'Agent'] },
		{ href: '/accounting', label: 'Accounting', icon: Calculator, roles: ['Admin', 'Manager'] },
		{ href: '/appointments', label: 'Appointments', icon: Calendar, roles: ['Admin', 'Manager', 'Agent'] },
		{ href: '/owners', label: 'Owners & Vendors', icon: BadgeDollarSign, roles: ['Admin', 'Manager'] },
		{ href: '/ai', label: 'AI Assistant', icon: Sparkles, roles: ['Admin', 'Manager', 'Agent'] },
		{ href: '/portal', label: 'Portal', icon: UserCircle2 },
		{ href: '/admin/users', label: 'User Access', icon: Shield, roles: ['Admin'] },
		{ href: '/settings', label: 'Settings', icon: Settings, roles: ['Admin', 'Manager'] }
	];

	let currentUser = $derived(getCurrentUser());
	let primaryRole = $derived(currentUser?.roles?.[0] ?? '');
	let visibleNavItems = $derived.by(() =>
		navItems.filter((item) => {
			if (!item.roles || item.roles.length === 0) return true;
			return hasAnyRole(...item.roles);
		})
	);

	function isActive(href: string, pathname: string): boolean {
		if (href === '/') return pathname === '/';
		return pathname.startsWith(href);
	}

	$effect(() => {
		const mql = window.matchMedia('(max-width: 768px)');
		const handleChange = (e: MediaQueryListEvent | MediaQueryList) => {
			isMobile = e.matches;
			if (e.matches) sidebarCollapsed = true;
		};
		handleChange(mql);
		mql.addEventListener('change', handleChange);
		return () => mql.removeEventListener('change', handleChange);
	});

	function handleNavClick() {
		if (isMobile) sidebarCollapsed = true;
	}
</script>

<div class="flex h-screen">
	<aside
		class="flex flex-col border-r border-border bg-card transition-all duration-200 {sidebarCollapsed
			? 'w-16'
			: 'w-60'} shrink-0"
	>
		<div class="flex items-center gap-2 border-b border-border px-4 py-4">
			<Building class="h-5 w-5 shrink-0 text-primary" />
			{#if !sidebarCollapsed}
				<span class="font-semibold text-foreground truncate">Rental Command</span>
			{/if}
		</div>

		<div class="border-b border-border py-2">
			<PortfolioSelector collapsed={sidebarCollapsed} />
		</div>

		<nav class="flex-1 space-y-1 px-2 py-3 overflow-y-auto">
			{#each visibleNavItems as item}
				{@const active = isActive(item.href, $page.url.pathname)}
				{@const Icon = item.icon}
				<a
					href={item.href}
					onclick={handleNavClick}
					class="flex items-center gap-3 rounded-md px-3 py-2 text-sm transition-colors
						{active
						? 'bg-primary/10 text-primary'
						: 'text-muted-foreground hover:bg-secondary hover:text-foreground'}
						{sidebarCollapsed ? 'justify-center' : ''}"
					title={sidebarCollapsed ? item.label : undefined}
				>
					<Icon class="h-4 w-4 shrink-0" />
					{#if !sidebarCollapsed}
						<span>{item.label}</span>
					{/if}
				</a>
			{/each}
		</nav>

		<div class="border-t border-border p-2">
			{#if !sidebarCollapsed && currentUser}
				<div class="mb-2 rounded-md border border-border bg-background px-3 py-2">
					<p class="truncate text-sm font-medium text-foreground">{currentUser.displayName}</p>
					<p class="text-xs text-muted-foreground">{primaryRole}</p>
				</div>
				<form method="POST" action="/logout">
					<button
						type="submit"
						class="mb-2 flex w-full items-center justify-center gap-2 rounded-md border border-border px-3 py-2 text-xs text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
					>
						<LogOut class="h-3.5 w-3.5" />
						<span>Sign out</span>
					</button>
				</form>
			{/if}
			<button
				onclick={() => (sidebarCollapsed = !sidebarCollapsed)}
				class="flex w-full items-center justify-center rounded-md p-2 text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
				title={sidebarCollapsed ? 'Expand sidebar' : 'Collapse sidebar'}
			>
				{#if sidebarCollapsed}
					<PanelLeftOpen class="h-4 w-4" />
				{:else}
					<PanelLeftClose class="h-4 w-4" />
				{/if}
			</button>
		</div>
	</aside>

	<main class="flex-1 overflow-hidden">
		{@render children()}
	</main>
</div>
