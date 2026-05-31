<script lang="ts">
	import { page } from '$app/stores';
	import {
		LayoutDashboard,
		ScanLine,
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
		LogOut,
		User,
		ChevronDown,
		Menu,
		X,
		Receipt,
		Landmark,
		FileBarChart
	} from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { Avatar, AvatarFallback } from '$lib/components/ui/avatar';
	import {
		DropdownMenu,
		DropdownMenuContent,
		DropdownMenuItem,
		DropdownMenuLabel,
		DropdownMenuSeparator,
		DropdownMenuTrigger
	} from '$lib/components/ui/dropdown-menu';
	import PortfolioSelector from '$lib/components/shared/PortfolioSelector.svelte';
	import { getCurrentUser, hasAnyRole, clearAuth } from '$lib/stores/auth.svelte';
	import NavigationLoader from '$lib/components/NavigationLoader.svelte';

	let { children }: { children: import('svelte').Snippet } = $props();

	let sidebarCollapsed = $state(false);
	let isMobile = $state(false);
	let isSidebarOpen = $state(false);

	// Responsive: auto-collapse on narrow viewports
	$effect(() => {
		const mql = window.matchMedia('(max-width: 1024px)');
		const handleChange = (e: MediaQueryListEvent | MediaQueryList) => {
			isMobile = e.matches;
			if (e.matches) sidebarCollapsed = true;
		};
		handleChange(mql);
		mql.addEventListener('change', handleChange);
		return () => mql.removeEventListener('change', handleChange);
	});

	type NavItem = {
		href: string;
		label: string;
		icon: typeof Building;
		roles?: string[];
	};

	const navItems: NavItem[] = [
		{ href: '/', label: 'Dashboard', icon: LayoutDashboard },
		{ href: '/scan', label: 'Scan', icon: ScanLine, roles: ['Admin', 'Manager', 'Agent'] },
		{ href: '/properties', label: 'Properties', icon: Building, roles: ['Admin', 'Manager', 'Agent'] },
		{ href: '/tenants', label: 'Tenants', icon: Users, roles: ['Admin', 'Manager', 'Agent'] },
		{ href: '/leases', label: 'Leases', icon: FileText, roles: ['Admin', 'Manager', 'Agent'] },
		{ href: '/maintenance', label: 'Maintenance', icon: Wrench, roles: ['Admin', 'Manager', 'Agent'] },
		{ href: '/accounting', label: 'Accounting', icon: Calculator, roles: ['Admin', 'Manager'] },
		{ href: '/deposits', label: 'Deposits', icon: Landmark, roles: ['Admin', 'Manager'] },
		{ href: '/tax', label: 'Tax', icon: Receipt, roles: ['Admin', 'Manager'] },
		{ href: '/owners-report', label: 'Owner Reports', icon: FileBarChart, roles: ['Admin', 'Manager'] },
		{ href: '/appointments', label: 'Appointments', icon: Calendar, roles: ['Admin', 'Manager', 'Agent'] },
		{ href: '/owners', label: 'Owners & Vendors', icon: BadgeDollarSign, roles: ['Admin', 'Manager'] },
		{ href: '/ai', label: 'AI Assistant', icon: Sparkles, roles: ['Admin', 'Manager', 'Agent'] },
		{ href: '/portal', label: 'Portal', icon: UserCircle2 },
		{ href: '/admin/users', label: 'User Access', icon: Shield, roles: ['Admin'] },
		{ href: '/settings', label: 'Settings', icon: Settings, roles: ['Admin', 'Manager'] }
	];

	let currentUser = $derived(getCurrentUser());
	let visibleNavItems = $derived.by(() =>
		navItems.filter((item) => {
			if (!item.roles || item.roles.length === 0) return true;
			return hasAnyRole(...item.roles);
		})
	);

	function isActive(href: string): boolean {
		const currentPath = $page.url.pathname;
		if (href === '/') return currentPath === '/';
		return currentPath.startsWith(href);
	}

	function getInitials(name: string): string {
		return name
			.split(' ')
			.map((n) => n[0])
			.join('')
			.toUpperCase()
			.slice(0, 2);
	}

	function handleNavClick() {
		if (isMobile) isSidebarOpen = false;
	}

	function signOut() {
		clearAuth();
	}
</script>

<NavigationLoader />

<div class="flex h-screen bg-background">
	<!-- Sidebar -->
	<aside
		class="fixed left-0 top-0 z-40 flex h-screen flex-col border-r border-sidebar-border bg-sidebar transition-all duration-200
			{isMobile
			? (isSidebarOpen ? 'translate-x-0 w-56' : '-translate-x-full w-56')
			: (sidebarCollapsed ? 'w-14' : 'w-56')}"
	>
		<!-- Logo / Brand -->
		<div class="flex h-14 items-center gap-2 border-b border-sidebar-border px-4">
			{#if sidebarCollapsed && !isMobile}
				<a href="/" class="flex items-center justify-center w-full">
					<Building class="h-5 w-5 text-primary" />
				</a>
			{:else}
				<a href="/" class="flex items-center gap-2">
					<Building class="h-5 w-5 shrink-0 text-primary" />
					<span class="font-semibold text-foreground truncate">Rental Command</span>
				</a>
			{/if}
		</div>

		<!-- Portfolio Selector -->
		<div class="border-b border-sidebar-border py-2">
			<PortfolioSelector collapsed={sidebarCollapsed && !isMobile} />
		</div>

		<!-- Navigation -->
		<nav class="flex-1 overflow-y-auto px-2 py-3" data-testid="main-nav">
			{#each visibleNavItems as item}
				{@const active = isActive(item.href)}
				<a
					href={item.href}
					onclick={handleNavClick}
					class="flex items-center gap-2 rounded-md px-3 py-1.5 text-sm transition-colors
						{active
						? 'bg-primary/10 text-primary font-medium'
						: 'text-muted-foreground hover:bg-sidebar-accent hover:text-sidebar-foreground'}
						{sidebarCollapsed && !isMobile ? 'justify-center' : ''}"
					title={sidebarCollapsed && !isMobile ? item.label : undefined}
					data-testid="nav-{item.href.replace('/', '').replace('/', '-') || 'dashboard'}"
				>
					<item.icon class="h-4 w-4 shrink-0" />
					{#if !sidebarCollapsed || isMobile}
						<span class="truncate">{item.label}</span>
					{/if}
				</a>
			{/each}
		</nav>

		<!-- User section at bottom -->
		<div class="border-t border-sidebar-border p-2">
			{#if sidebarCollapsed && !isMobile}
				<!-- Collapsed: avatar only -->
				<DropdownMenu>
					<DropdownMenuTrigger
						class="flex w-full items-center justify-center rounded-md px-2 py-2 text-sidebar-foreground transition-colors hover:bg-sidebar-accent"
						data-testid="user-menu"
					>
						<Avatar class="h-7 w-7">
							<AvatarFallback class="bg-primary text-xs text-primary-foreground">
								{getInitials(currentUser?.displayName || 'U')}
							</AvatarFallback>
						</Avatar>
					</DropdownMenuTrigger>
					<DropdownMenuContent class="w-56" align="start" side="right">
						<DropdownMenuLabel class="font-normal">
							<div class="flex flex-col space-y-1">
								<p class="text-sm font-medium">{currentUser?.displayName}</p>
								<p class="text-xs text-muted-foreground">{currentUser?.email}</p>
							</div>
						</DropdownMenuLabel>
						<DropdownMenuSeparator />
						<DropdownMenuItem data-testid="user-menu-settings">
							<a href="/settings" class="flex w-full items-center gap-2">
								<Settings class="h-4 w-4" />
								Settings
							</a>
						</DropdownMenuItem>
						<DropdownMenuSeparator />
						<DropdownMenuItem
							class="text-destructive focus:text-destructive"
							data-testid="user-menu-sign-out-collapsed"
							onSelect={signOut}
						>
							<LogOut class="h-4 w-4" />
							<span>Sign Out</span>
						</DropdownMenuItem>
					</DropdownMenuContent>
				</DropdownMenu>
			{:else}
				<!-- Expanded: full user card -->
				<DropdownMenu>
					<DropdownMenuTrigger
						class="flex h-auto w-full items-center justify-start gap-3 rounded-md px-2 py-2 text-sidebar-foreground transition-colors hover:bg-sidebar-accent"
						data-testid="user-menu"
					>
						<Avatar class="h-7 w-7">
							<AvatarFallback class="bg-primary text-xs text-primary-foreground">
								{getInitials(currentUser?.displayName || 'U')}
							</AvatarFallback>
						</Avatar>
						<div class="flex flex-1 flex-col items-start min-w-0">
							<span class="text-sm font-medium truncate w-full">{currentUser?.displayName}</span>
							<span class="text-xs text-muted-foreground truncate w-full">{currentUser?.email}</span>
						</div>
						<ChevronDown class="h-4 w-4 shrink-0 text-muted-foreground" />
					</DropdownMenuTrigger>
					<DropdownMenuContent class="w-56" align="start" side="top">
						<DropdownMenuLabel class="font-normal">
							<div class="flex flex-col space-y-1">
								<p class="text-sm font-medium">{currentUser?.displayName}</p>
								<p class="text-xs text-muted-foreground">{currentUser?.email}</p>
							</div>
						</DropdownMenuLabel>
						<DropdownMenuSeparator />
						<DropdownMenuItem data-testid="user-menu-settings">
							<a href="/settings" class="flex w-full items-center gap-2">
								<Settings class="h-4 w-4" />
								Settings
							</a>
						</DropdownMenuItem>
						<DropdownMenuSeparator />
						<DropdownMenuItem
							class="text-destructive focus:text-destructive"
							data-testid="user-menu-sign-out"
							onSelect={signOut}
						>
							<LogOut class="h-4 w-4" />
							<span>Sign Out</span>
						</DropdownMenuItem>
					</DropdownMenuContent>
				</DropdownMenu>
			{/if}

			<!-- Collapse toggle -->
			{#if !isMobile}
				<button
					onclick={() => (sidebarCollapsed = !sidebarCollapsed)}
					data-testid="sidebar-toggle"
					class="mt-1 flex w-full items-center gap-2 rounded-md px-3 py-2 text-sm text-muted-foreground transition-colors hover:bg-sidebar-accent hover:text-sidebar-foreground {sidebarCollapsed ? 'justify-center' : ''}"
				>
					{#if sidebarCollapsed}
						<PanelLeftOpen class="h-4 w-4 shrink-0" />
					{:else}
						<PanelLeftClose class="h-4 w-4 shrink-0" />
						<span>Collapse</span>
					{/if}
				</button>
			{/if}
		</div>
	</aside>

	<!-- Main content -->
	<div
		class="flex-1 min-w-0 flex flex-col transition-all duration-200 {isMobile
			? 'ml-0'
			: sidebarCollapsed
				? 'ml-14'
				: 'ml-56'}"
	>
		<!-- Mobile top bar -->
		{#if isMobile}
			<header
				class="sticky top-0 z-30 flex h-14 items-center gap-4 border-b border-border bg-background/95 px-4 backdrop-blur supports-[backdrop-filter]:bg-background/60"
			>
				<Button
					variant="ghost"
					size="icon"
					onclick={() => (isSidebarOpen = !isSidebarOpen)}
					data-testid="mobile-menu-toggle"
					aria-label="Toggle navigation menu"
				>
					{#if isSidebarOpen}
						<X class="h-5 w-5" />
					{:else}
						<Menu class="h-5 w-5" />
					{/if}
				</Button>
				<span class="font-semibold text-foreground">Rental Command</span>
			</header>
		{/if}

		<!-- Page content -->
		<main class="flex-1 overflow-auto p-6">
			<div class="pb-10">
				{@render children()}
			</div>
		</main>
	</div>
</div>

<!-- Mobile overlay -->
{#if isMobile && isSidebarOpen}
	<button
		type="button"
		class="fixed inset-0 z-30 bg-black/60 backdrop-blur-sm"
		onclick={() => (isSidebarOpen = false)}
		aria-label="Close sidebar"
		data-testid="sidebar-overlay"
	></button>
{/if}
