<script lang="ts">
	import '../app.css';
	import { goto } from '$app/navigation';
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
		PanelLeftOpen,
		Shield,
		UserCircle2,
		LogOut
	} from '@lucide/svelte';
	import PortfolioSelector from '$lib/components/shared/PortfolioSelector.svelte';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { auth } from '$lib/api/endpoints/auth';
	import { authToken } from '$lib/api/client';
	import { clearAuth, getCurrentUser, hasAnyRole, setCurrentUser } from '$lib/stores/auth.svelte';
	import type { UserRole } from '$lib/types';

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
	let authReady = $state(false);

	const navItems: { href: string; label: string; icon: any; roles?: UserRole[] }[] = [
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
		{ href: '/admin/users', label: 'User Access', icon: Shield, roles: ['Admin', 'Manager'] },
		{ href: '/settings', label: 'Settings', icon: Settings, roles: ['Admin', 'Manager'] },
	];

	let currentUser = $derived(getCurrentUser());
	let isLoginRoute = $derived($page.url.pathname === '/login');
	let visibleNavItems = $derived.by(() => {
		return navItems.filter((item) => {
			if (!item.roles || item.roles.length === 0) return true;
			return hasAnyRole(...item.roles);
		});
	});

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

	async function logout() {
		try {
			await auth.logout();
		} finally {
			clearAuth();
			window.location.href = '/login';
		}
	}

	$effect(() => {
		const token = authToken.get();
		if (!token) {
			clearAuth();
			authReady = true;
			return;
		}

		auth.me()
			.then((me) => setCurrentUser(me))
			.catch(() => clearAuth())
			.finally(() => {
				authReady = true;
			});
	});

	$effect(() => {
		if (!authReady) return;
		if (!currentUser && !isLoginRoute) goto('/login');
		if (currentUser && isLoginRoute) goto('/portal');
	});

	$effect(() => {
		if (!currentUser || isLoginRoute) return;

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
	{#if isLoginRoute}
		<main class="h-screen overflow-hidden">
			{@render children()}
		</main>
	{:else if !authReady || !currentUser}
		<main class="flex h-screen items-center justify-center text-text-tertiary">Loading...</main>
	{:else}
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
					{#each visibleNavItems as item}
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
					{#if !sidebarCollapsed && currentUser}
						<div class="mb-2 rounded-md border border-border bg-bg px-3 py-2">
							<p class="truncate text-sm font-medium text-text-primary">{currentUser.displayName}</p>
							<p class="text-xs text-text-secondary">{currentUser.role}</p>
						</div>
						<button
							onclick={logout}
							class="mb-2 flex w-full items-center justify-center gap-2 rounded-md border border-border px-3 py-2 text-xs text-text-secondary transition-colors hover:bg-surface-hover hover:text-text-primary"
						>
							<LogOut class="h-3.5 w-3.5" />
							<span>Sign out</span>
						</button>
					{/if}
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
	{/if}
</QueryClientProvider>
