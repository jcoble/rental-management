<script lang="ts">
	import '../app.css';
	import { page } from '$app/stores';
	import { QueryClient, QueryClientProvider } from '@tanstack/svelte-query';
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
		PanelLeftOpen
	} from '@lucide/svelte';
	import PortfolioSelector from '$lib/components/shared/PortfolioSelector.svelte';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';

	let { children } = $props();

	const queryClient = new QueryClient({
		defaultOptions: {
			queries: {
				staleTime: 15000,
				refetchOnWindowFocus: false,
			},
		},
	});

	let sidebarCollapsed = $state(false);
	let isMobile = $state(false);

	const navItems = [
		{ href: '/', label: 'Dashboard', icon: LayoutDashboard },
		{ href: '/properties', label: 'Properties', icon: Building },
		{ href: '/tenants', label: 'Tenants', icon: Users },
		{ href: '/leases', label: 'Leases', icon: FileText },
		{ href: '/maintenance', label: 'Maintenance', icon: Wrench },
		{ href: '/accounting', label: 'Accounting', icon: Calculator },
		{ href: '/appointments', label: 'Appointments', icon: Calendar },
		{ href: '/owners', label: 'Owners & Vendors', icon: BadgeDollarSign },
		{ href: '/ai', label: 'AI Assistant', icon: Sparkles },
		{ href: '/settings', label: 'Settings', icon: Settings },
	];

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

	$effect(() => {
		const portfolioId = getCurrentPortfolioId();
		const eventSource = new EventSource(`/api/events?portfolioId=${portfolioId}`);

		const invalidateAll = () => {
			queryClient.invalidateQueries();
		};

		const events = [
			'portfolio:created',
			'portfolio:updated',
			'property:created',
			'property:updated',
			'unit:created',
			'unit:updated',
			'tenant:created',
			'tenant:updated',
			'lease:created',
			'lease:updated',
			'payment:created',
			'payment:updated',
			'expense:created',
			'expense:updated',
			'work-order:created',
			'work-order:updated',
			'appointment:created',
			'appointment:updated',
			'inspection:created',
			'inspection:updated',
			'owner:created',
			'owner:updated',
			'vendor:created',
			'vendor:updated',
		];

		for (const evt of events) {
			eventSource.addEventListener(evt, invalidateAll);
		}

		return () => eventSource.close();
	});
</script>

<QueryClientProvider client={queryClient}>
	<div class="flex h-screen">
		<aside
			class="flex flex-col border-r border-border bg-surface transition-all duration-200 {sidebarCollapsed ? 'w-16' : 'w-60'} shrink-0"
		>
			<div class="flex items-center gap-2 border-b border-border px-4 py-4">
				<Building class="h-5 w-5 shrink-0 text-accent" />
				{#if !sidebarCollapsed}
					<span class="font-semibold text-text-primary truncate">Rental Command</span>
				{/if}
			</div>

			<div class="border-b border-border py-2">
				<PortfolioSelector collapsed={sidebarCollapsed} />
			</div>

			<nav class="flex-1 space-y-1 px-2 py-3 overflow-y-auto">
				{#each navItems as item}
					{@const active = isActive(item.href, $page.url.pathname)}
					{@const Icon = item.icon}
					<a
						href={item.href}
						onclick={handleNavClick}
						class="flex items-center gap-3 rounded-md px-3 py-2 text-sm transition-colors
							{active
							? 'bg-accent/10 text-accent'
							: 'text-text-secondary hover:bg-surface-hover hover:text-text-primary'}
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
				<button
					onclick={() => (sidebarCollapsed = !sidebarCollapsed)}
					class="flex w-full items-center justify-center rounded-md p-2 text-text-secondary transition-colors hover:bg-surface-hover hover:text-text-primary"
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
</QueryClientProvider>
