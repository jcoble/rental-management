<script lang="ts">
	import { untrack } from 'svelte';
	import { page } from '$app/stores';
	import { browser } from '$app/environment';
	import { createQuery } from '@tanstack/svelte-query';
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
		LogOut,
		ChevronDown,
		ChevronRight,
		Menu,
		X,
		Receipt,
		Landmark,
		FileBarChart,
		History,
		BarChart3,
		MessageSquare,
		CreditCard,
		ClipboardList,
		Home,
		BellRing,
		Rocket,
		FileSpreadsheet,
		PiggyBank,
		Compass,
		Briefcase,
		Wallet,
		Contact,
		Bot,
		BookOpen,
		HelpCircle
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
	import NotificationBell from '$lib/components/notifications/NotificationBell.svelte';
	import { getCurrentUser, hasAnyRole, clearAuth, getAuthState } from '$lib/stores/auth.svelte';
	import { isPortalUser, isStaff } from '$lib/types/user';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { messages as messagesApi } from '$lib/api/endpoints/messages';
	import { appointments as appointmentsApi } from '$lib/api/endpoints/appointments';
	import NavigationLoader from '$lib/components/NavigationLoader.svelte';
	import AssistantBubble from '$lib/components/assistant/AssistantBubble.svelte';
	import SandboxBanner from '$lib/components/SandboxBanner.svelte';

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

	type NavGroup = {
		id: string;
		label: string;
		icon: typeof Building;
		items: NavItem[];
	};

	// Grouped navigation (TODO #1). Each group is collapsible; the active route's
	// group auto-expands and open/closed state is remembered per group.
	const staffNavGroups: NavGroup[] = [
		{
			id: 'overview',
			label: 'Overview',
			icon: Compass,
			items: [
				{ href: '/', label: 'Dashboard', icon: LayoutDashboard },
				{ href: '/analytics', label: 'Insights', icon: BarChart3, roles: ['Admin', 'Manager'] }
			]
		},
		{
			id: 'get-started',
			label: 'Get Started',
			icon: Rocket,
			items: [
				{ href: '/onboarding', label: 'Setup', icon: Rocket, roles: ['Admin', 'Manager'] },
				{ href: '/import', label: 'Import Data', icon: FileSpreadsheet, roles: ['Admin', 'Manager'] },
				{ href: '/scan', label: 'Scan', icon: ScanLine, roles: ['Admin', 'Manager', 'Agent'] }
			]
		},
		{
			id: 'portfolio',
			label: 'Portfolio',
			icon: Briefcase,
			items: [
				{ href: '/properties', label: 'Properties', icon: Building, roles: ['Admin', 'Manager', 'Agent'] },
				{ href: '/tenants', label: 'Tenants', icon: Users, roles: ['Admin', 'Manager', 'Agent'] },
				{ href: '/leases', label: 'Leases', icon: FileText, roles: ['Admin', 'Manager', 'Agent'] },
				{ href: '/applications', label: 'Applications', icon: ClipboardList, roles: ['Admin', 'Manager', 'Agent'] },
				{ href: '/notices', label: 'Notices', icon: MessageSquare, roles: ['Admin', 'Manager', 'Agent'] }
			]
		},
		{
			id: 'operations',
			label: 'Operations',
			icon: Wrench,
			items: [
				{ href: '/maintenance', label: 'Maintenance', icon: Wrench, roles: ['Admin', 'Manager', 'Agent'] },
				{ href: '/appointments', label: 'Appointments', icon: Calendar, roles: ['Admin', 'Manager', 'Agent'] },
				{ href: '/messages', label: 'Messages', icon: MessageSquare, roles: ['Admin', 'Manager', 'Agent'] }
			]
		},
		{
			id: 'money',
			label: 'Money',
			icon: Wallet,
			items: [
				{ href: '/accounting', label: 'Accounting', icon: Calculator, roles: ['Admin', 'Manager'] },
				{ href: '/reports', label: 'Reports', icon: BarChart3, roles: ['Admin', 'Manager'] },
				{ href: '/banking', label: 'Banking', icon: Landmark, roles: ['Admin', 'Manager'] },
				{ href: '/deposits', label: 'Deposits', icon: PiggyBank, roles: ['Admin', 'Manager'] },
				{ href: '/tax', label: 'Tax', icon: Receipt, roles: ['Admin', 'Manager'] },
				{ href: '/owners-report', label: 'Owner Reports', icon: FileBarChart, roles: ['Admin', 'Manager'] }
			]
		},
		{
			id: 'directory',
			label: 'Directory',
			icon: Contact,
			items: [{ href: '/owners', label: 'Owners & Vendors', icon: BadgeDollarSign, roles: ['Admin', 'Manager'] }]
		},
		{
			id: 'ai',
			label: 'AI & Help',
			icon: Bot,
			items: [
				{ href: '/ai', label: 'AI Assistant', icon: Sparkles, roles: ['Admin', 'Manager', 'Agent'] },
				{ href: '/docs', label: 'Help & Docs', icon: BookOpen }
			]
		},
		{
			id: 'admin',
			label: 'Administration',
			icon: Shield,
			items: [
				{ href: '/activity', label: 'Activity', icon: History, roles: ['Admin', 'Manager'] },
				{ href: '/admin/users', label: 'User Access', icon: Shield, roles: ['Admin'] },
				{ href: '/settings', label: 'Settings', icon: Settings, roles: ['Admin', 'Manager'] }
			]
		}
	];

	let currentUser = $derived(getCurrentUser());
	let portalUser = $derived(isPortalUser(currentUser) && !isStaff(currentUser));

	const portalNavItems: NavItem[] = [
		{ href: '/portal', label: 'Dashboard', icon: Home },
		{ href: '/portal/messages', label: 'Messages', icon: MessageSquare },
		{ href: '/portal/notifications', label: 'Notifications', icon: BellRing },
		{ href: '/portal/maintenance', label: 'Maintenance', icon: Wrench },
		{ href: '/portal/payments', label: 'Payments', icon: CreditCard },
		{ href: '/portal/lease', label: 'Lease', icon: FileText },
		{ href: '/portal/appointments', label: 'Appointments', icon: Calendar },
		{ href: '/portal/requests', label: 'Requests', icon: ClipboardList }
	];

	function itemVisible(item: NavItem): boolean {
		if (!item.roles || item.roles.length === 0) return true;
		return hasAnyRole(...item.roles);
	}

	// Staff groups filtered to the items the current user may see; empty groups dropped.
	let visibleGroups = $derived.by(() =>
		staffNavGroups
			.map((g) => ({ ...g, items: g.items.filter(itemVisible) }))
			.filter((g) => g.items.length > 0)
	);

	// Flat list of every visible nav item (both modes) for title resolution.
	let allItems = $derived.by(() =>
		portalUser ? portalNavItems : visibleGroups.flatMap((g) => g.items)
	);

	function isActive(href: string): boolean {
		const currentPath = $page.url.pathname;
		if (href === '/') return currentPath === '/';
		return currentPath.startsWith(href);
	}

	function groupHasActive(group: NavGroup): boolean {
		return group.items.some((i) => isActive(i.href));
	}

	// --- Collapsible group open/closed state (remembered) -----------------------
	const STORAGE_KEY = 'rc.nav.groups';
	let openGroups = $state<Record<string, boolean>>({});

	$effect(() => {
		if (!browser) return;
		try {
			const raw = localStorage.getItem(STORAGE_KEY);
			// untrack the openGroups read: this effect HYDRATES openGroups from storage, so it must not
			// also depend on openGroups — otherwise its own write retriggers it forever
			// (effect_update_depth_exceeded → the whole app freezes). With untrack it runs once on mount.
			if (raw) openGroups = { ...untrack(() => openGroups), ...JSON.parse(raw) };
		} catch {
			/* ignore malformed storage */
		}
	});

	// Default any group with no stored preference to open, and always force-open
	// the group that contains the active route. Reads openGroups untracked (it WRITES openGroups, and
	// should only re-run when the route / visible groups change — not on its own write).
	$effect(() => {
		const next = { ...untrack(() => openGroups) };
		let changed = false;
		for (const g of visibleGroups) {
			if (next[g.id] === undefined) {
				next[g.id] = true;
				changed = true;
			}
			if (groupHasActive(g) && next[g.id] !== true) {
				next[g.id] = true;
				changed = true;
			}
		}
		if (changed) openGroups = next;
	});

	function toggleGroup(id: string) {
		const next = { ...openGroups, [id]: !openGroups[id] };
		openGroups = next;
		if (browser) {
			try {
				localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
			} catch {
				/* storage may be unavailable */
			}
		}
	}

	// Title shown in the mobile top bar — the label of the deepest matching nav item.
	let currentTitle = $derived.by(() => {
		const match = allItems
			.filter((item) => isActive(item.href))
			.sort((a, b) => b.href.length - a.href.length)[0];
		return match?.label ?? 'Rental Command';
	});

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

	// --- Header quick-action live counts ---------------------------------------
	const auth = getAuthState();
	const showStaffHeader = $derived(!portalUser);

	const unreadMessagesQuery = createQuery(() => ({
		queryKey: ['header-unread-messages'],
		enabled: auth.isAuthenticated && !portalUser && hasAnyRole('Admin', 'Manager', 'Agent'),
		queryFn: () => messagesApi.list(),
		staleTime: 30_000,
		refetchInterval: 60_000
	}));
	let unreadMessages = $derived.by(() => {
		const list = unreadMessagesQuery.data ?? [];
		return list.reduce((sum, c) => sum + (c.unreadCount ?? 0), 0);
	});

	const upcomingApptsQuery = createQuery(() => ({
		queryKey: ['header-upcoming-appointments', getCurrentPortfolioId()],
		enabled: auth.isAuthenticated && !portalUser && hasAnyRole('Admin', 'Manager', 'Agent'),
		queryFn: () => appointmentsApi.list(getCurrentPortfolioId(), { take: 100 }),
		staleTime: 60_000,
		refetchInterval: 120_000
	}));
	let upcomingAppts = $derived.by(() => {
		const list = upcomingApptsQuery.data ?? [];
		const now = Date.now();
		const horizon = now + 7 * 24 * 60 * 60 * 1000;
		return list.filter((a) => {
			if (a.status === 'Cancelled' || a.status === 'Completed' || a.status === 'NoShow') return false;
			const t = new Date(a.scheduledStart).getTime();
			return t >= now && t <= horizon;
		}).length;
	});
</script>

<NavigationLoader />

{#snippet countBadge(count: number)}
	{#if count > 0}
		<span
			class="absolute -right-0.5 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-primary px-1 text-[10px] font-bold text-primary-foreground"
			aria-hidden="true"
		>
			{count > 99 ? '99+' : count}
		</span>
	{/if}
{/snippet}

<div class="flex h-full w-full overflow-hidden bg-background">
	<!-- Sidebar -->
	<aside
		class="fixed left-0 top-0 z-40 flex h-full flex-col border-r border-sidebar-border bg-sidebar transition-all duration-200
			{isMobile
			? (isSidebarOpen ? 'translate-x-0 w-60' : '-translate-x-full w-60')
			: (sidebarCollapsed ? 'w-14' : 'w-60')}"
	>
		<!-- Logo / Brand -->
		<div class="flex h-14 items-center gap-2 border-b border-sidebar-border px-4">
			{#if sidebarCollapsed && !isMobile}
				<a href="/" class="flex w-full items-center justify-center">
					<Building class="h-5 w-5 text-primary" />
				</a>
			{:else}
				<a href="/" class="flex items-center gap-2">
					<span class="flex h-7 w-7 items-center justify-center rounded-lg bg-primary/10 text-primary ring-1 ring-inset ring-primary/20">
						<Building class="h-4 w-4" />
					</span>
					<span class="truncate font-semibold tracking-tight text-foreground">Rental Command</span>
				</a>
			{/if}
		</div>

		<!-- Portfolio Selector -->
		{#if !portalUser}
			<div class="border-b border-sidebar-border py-2">
				<PortfolioSelector collapsed={sidebarCollapsed && !isMobile} />
			</div>
		{/if}

		<!-- Navigation -->
		<nav class="flex-1 overflow-y-auto px-2 py-3" data-testid="main-nav">
			{#if portalUser}
				{#each portalNavItems as item}
					{@const active = isActive(item.href)}
					<a
						href={item.href}
						onclick={handleNavClick}
						class="flex items-center gap-2 rounded-md px-3 py-1.5 text-sm transition-colors
							{active
							? 'bg-primary/10 font-medium text-primary'
							: 'text-muted-foreground hover:bg-sidebar-accent hover:text-sidebar-foreground'}
							{sidebarCollapsed && !isMobile ? 'justify-center' : ''}"
						title={sidebarCollapsed && !isMobile ? item.label : undefined}
						data-testid="nav-{item.href.replace('/', '').replace('/', '-') || 'dashboard'}"
					>
						<item.icon class="h-4 w-4 shrink-0 {active ? 'text-primary' : ''}" />
						{#if !sidebarCollapsed || isMobile}
							<span class="truncate">{item.label}</span>
						{/if}
					</a>
				{/each}
			{:else if sidebarCollapsed && !isMobile}
				<!-- Collapsed rail: flat icon list, grouping hidden -->
				{#each visibleGroups as group}
					{#each group.items as item}
						{@const active = isActive(item.href)}
						<a
							href={item.href}
							onclick={handleNavClick}
							class="flex items-center justify-center rounded-md px-3 py-1.5 text-sm transition-colors
								{active
								? 'bg-primary/10 text-primary'
								: 'text-muted-foreground hover:bg-sidebar-accent hover:text-sidebar-foreground'}"
							title={item.label}
							data-testid="nav-{item.href.replace('/', '').replace('/', '-') || 'dashboard'}"
						>
							<item.icon class="h-4 w-4 shrink-0" />
						</a>
					{/each}
				{/each}
			{:else}
				<!-- Expanded: collapsible groups -->
				{#each visibleGroups as group}
					{@const open = openGroups[group.id] ?? true}
					<div class="mb-0.5">
						<button
							type="button"
							onclick={() => toggleGroup(group.id)}
							class="flex w-full items-center gap-2 rounded-md px-3 py-1.5 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground/70 transition-colors hover:text-sidebar-foreground"
							aria-expanded={open}
							data-testid="nav-group-{group.id}"
						>
							<group.icon class="h-3.5 w-3.5 shrink-0 opacity-70" />
							<span class="flex-1 truncate text-left">{group.label}</span>
							{#if open}
								<ChevronDown class="h-3.5 w-3.5 shrink-0 opacity-60" />
							{:else}
								<ChevronRight class="h-3.5 w-3.5 shrink-0 opacity-60" />
							{/if}
						</button>
						{#if open}
							<div class="mb-1 ml-2 space-y-0.5 border-l border-sidebar-border pl-2">
								{#each group.items as item}
									{@const active = isActive(item.href)}
									<a
										href={item.href}
										onclick={handleNavClick}
										class="flex items-center gap-2 rounded-md px-3 py-1.5 text-sm transition-colors
											{active
											? 'bg-primary/10 font-medium text-primary'
											: 'text-muted-foreground hover:bg-sidebar-accent hover:text-sidebar-foreground'}"
										data-testid="nav-{item.href.replace('/', '').replace('/', '-') || 'dashboard'}"
									>
										<item.icon class="h-4 w-4 shrink-0 {active ? 'text-primary' : ''}" />
										<span class="truncate">{item.label}</span>
									</a>
								{/each}
							</div>
						{/if}
					</div>
				{/each}
			{/if}
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
						{#if !portalUser}
							<DropdownMenuItem data-testid="user-menu-settings">
								<a href="/settings" class="flex w-full items-center gap-2">
									<Settings class="h-4 w-4" />
									Settings
								</a>
							</DropdownMenuItem>
							<DropdownMenuSeparator />
						{/if}
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
						<div class="flex min-w-0 flex-1 flex-col items-start">
							<span class="w-full truncate text-sm font-medium">{currentUser?.displayName}</span>
							<span class="w-full truncate text-xs text-muted-foreground">{currentUser?.email}</span>
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
						{#if !portalUser}
							<DropdownMenuItem data-testid="user-menu-settings">
								<a href="/settings" class="flex w-full items-center gap-2">
									<Settings class="h-4 w-4" />
									Settings
								</a>
							</DropdownMenuItem>
							<DropdownMenuSeparator />
						{/if}
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
		class="flex h-full min-w-0 flex-1 flex-col transition-all duration-200 {isMobile
			? 'ml-0'
			: sidebarCollapsed
				? 'ml-14'
				: 'ml-60'}"
	>
		<!-- Sandbox mode banner: slim, top of the shell, above the header. Hidden when Live. -->
		{#if !portalUser}
			<SandboxBanner variant="banner" />
		{/if}

		<!-- App header bar: quick actions + live badges (TODO #2). Always visible. -->
		<header
			class="sticky top-0 z-30 flex h-14 items-center gap-2 border-b border-border bg-background/95 px-3 backdrop-blur supports-[backdrop-filter]:bg-background/60 sm:px-4"
		>
			{#if isMobile}
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
			{/if}

			<span
				class="truncate text-sm font-semibold tracking-tight text-foreground"
				data-testid="page-title"
			>
				{currentTitle}
			</span>

			<div class="ml-auto flex items-center gap-1">
				{#if showStaffHeader}
					<!-- Scan -->
					<a
						href="/scan"
						class="relative flex items-center justify-center rounded-md p-2 text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
						aria-label="Scan a document"
						title="Scan a document"
						data-testid="header-scan"
					>
						<ScanLine class="h-5 w-5" />
					</a>

					<!-- Messages -->
					<a
						href="/messages"
						class="relative flex items-center justify-center rounded-md p-2 text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
						aria-label="Messages{unreadMessages > 0 ? ` (${unreadMessages} unread)` : ''}"
						title="Messages"
						data-testid="header-messages"
					>
						<MessageSquare class="h-5 w-5" />
						{@render countBadge(unreadMessages)}
					</a>

					<!-- Appointments -->
					<a
						href="/appointments"
						class="relative flex items-center justify-center rounded-md p-2 text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
						aria-label="Appointments{upcomingAppts > 0 ? ` (${upcomingAppts} upcoming)` : ''}"
						title="Upcoming appointments"
						data-testid="header-appointments"
					>
						<Calendar class="h-5 w-5" />
						{@render countBadge(upcomingAppts)}
					</a>
				{/if}

				<!-- Help & Docs -->
				<a
					href="/docs"
					class="relative flex items-center justify-center rounded-md p-2 text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
					aria-label="Help and documentation"
					title="Help & Docs"
					data-testid="header-help"
				>
					<HelpCircle class="h-5 w-5" />
				</a>

				<!-- Notifications -->
				<NotificationBell data-testid="notification-bell-header" placement="down" />
			</div>
		</header>

		<!-- Page content frame. Routes own their internal 100% scroll area. -->
		<main class="flex min-h-0 flex-1 justify-center overflow-hidden">
			<div class="h-full w-full max-w-[1600px]">
				{@render children()}
			</div>
		</main>
		<AssistantBubble />
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
