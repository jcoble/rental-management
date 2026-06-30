<script lang="ts">
	import { untrack } from 'svelte';
	import { page } from '$app/state';
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
		Menu,
		X,
		History,
		BarChart3,
		MessageSquare,
		CreditCard,
		ClipboardList,
		Home,
		BellRing,
		Briefcase,
		Wallet,
		PiggyBank,
		Contact,
		BookOpen,
		HelpCircle,
		Activity,
		Upload
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
	import M3NavGroup from '$lib/components/m3/NavGroup.svelte';
	import M3NavItem from '$lib/components/m3/NavItem.svelte';
	import MaterialSymbol from '$lib/components/m3/MaterialSymbol.svelte';
	import CommandCenterNav from '$lib/components/CommandCenterNav.svelte';
	import { clearAuthState } from '$lib/stores/auth.svelte';
	import { hasRole, isPortalUser, isStaff } from '$lib/types/user';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { messages as messagesApi } from '$lib/api/endpoints/messages';
	import { appointments as appointmentsApi } from '$lib/api/endpoints/appointments';
	import NavigationLoader from '$lib/components/NavigationLoader.svelte';
	import SandboxBanner from '$lib/components/SandboxBanner.svelte';
	import M3TooltipLayer from '$lib/components/shared/M3TooltipLayer.svelte';
	import ThemeModeToggle from '$lib/components/shared/ThemeModeToggle.svelte';
	import ScanLauncher from '$lib/components/scan/ScanLauncher.svelte';

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

	// Pinned single links above all groups (IA Wave 1 §4.2): the dashboard + the flagship
	// "Scan / Edit" action, one tap away and outside any group.
	const pinnedNavItems: NavItem[] = [
		{ href: '/', label: 'Dashboard', icon: LayoutDashboard },
		{ href: '/onboarding', label: 'Guided Setup', icon: ClipboardList },
		{ href: '/scan', label: 'Scan / Edit', icon: ScanLine, roles: ['Admin', 'Manager', 'Agent'] }
	];

	// Grouped navigation (IA Wave 1 §4.2): four landlord-noun groups in frequency order —
	// Money → Rentals → Work → Inbox. Each group is collapsible; the active route's group
	// auto-expands and open/closed state is remembered per group.
	const staffNavGroups: NavGroup[] = [
		{
			id: 'money',
			label: 'Money',
			icon: Wallet,
			items: [
				{ href: '/accounting', label: 'Money', icon: Calculator, roles: ['Admin', 'Manager'] },
				{ href: '/deposits', label: 'Security Deposits', icon: PiggyBank, roles: ['Admin', 'Manager'] },
				{ href: '/reports', label: 'Reports', icon: BarChart3, roles: ['Admin', 'Manager'] }
			]
		},
		{
			id: 'rentals',
			label: 'Rentals',
			icon: Briefcase,
			items: [
				{ href: '/properties', label: 'Properties', icon: Building, roles: ['Admin', 'Manager', 'Agent'] },
				{ href: '/units', label: 'Units', icon: Home, roles: ['Admin', 'Manager', 'Agent'] },
				{ href: '/tenants', label: 'Tenants', icon: Users, roles: ['Admin', 'Manager', 'Agent'] },
				{ href: '/leases', label: 'Leases', icon: FileText, roles: ['Admin', 'Manager', 'Agent'] },
				{ href: '/lease-templates', label: 'Lease Templates', icon: Upload, roles: ['Admin', 'Manager', 'Agent'] },
				{ href: '/applications', label: 'Applications', icon: ClipboardList, roles: ['Admin', 'Manager', 'Agent'] }
			]
		},
		{
			id: 'work',
			label: 'Work',
			icon: Wrench,
			items: [
				// A6: one professional term for the "things to fix" concept — "Work Orders" — used
				// consistently across the staff nav, the page heading, and the dashboard.
				{ href: '/maintenance', label: 'Work Orders', icon: Wrench, roles: ['Admin', 'Manager', 'Agent'] },
				{ href: '/appointments', label: 'Appointments', icon: Calendar, roles: ['Admin', 'Manager', 'Agent'] },
				{ href: '/vendors', label: 'Vendors', icon: Contact, roles: ['Admin', 'Manager'] }
			]
		},
		{
			id: 'inbox',
			label: 'Inbox',
			icon: MessageSquare,
			items: [
				{ href: '/messages', label: 'Messages', icon: MessageSquare, roles: ['Admin', 'Manager', 'Agent'] },
				{ href: '/notices', label: 'Tenant notices', icon: BellRing, roles: ['Admin', 'Manager', 'Agent'] }
			]
		}
	];

	// Bottom rail (IA Wave 1 §4.2): Assistant (ambient), Help, and the Settings hub. The
	// Settings hub's internal section split is Wave 4 — for now the relocated Administration
	// entries (Team, Owners, Activity history) live under it as sub-links.
	// A4: ONE AI entry point. "Ask" (→ /ai) is the single doorway to the assistant; the redundant
	// floating AssistantBubble was removed from this shell so there aren't multiple competing doorways.
	const bottomRailItems: NavItem[] = [
		{ href: '/ai', label: 'Ask', icon: Sparkles, roles: ['Admin', 'Manager', 'Agent'] },
		{ href: '/docs', label: 'Help', icon: BookOpen }
	];

	// Settings hub group (relocated from the old "Administration" group). Owners + Team move
	// here per §4.3; Engine Health/forensic audit are gone (super-admin shell + Advanced toggle).
	const settingsGroup: NavGroup = {
		id: 'settings',
		label: 'Settings',
		icon: Settings,
		items: [
			{ href: '/settings', label: 'Settings', icon: Settings, roles: ['Admin', 'Manager'] },
			{ href: '/admin/users', label: 'Team', icon: Shield, roles: ['Admin'] },
			{ href: '/owners', label: 'Owners', icon: BadgeDollarSign, roles: ['Admin', 'Manager'] },
			{ href: '/audit', label: 'Activity history', icon: History, roles: ['Admin', 'Manager'] }
		]
	};

	// Material Symbols Rounded glyph per nav href. Drives the active outline→fill
	// morph (the active item flips --msym-fill 0→1). Keyed by href so the existing
	// Lucide `icon` fields stay as a fallback for portal nav and elsewhere. Every
	// glyph here MUST be in the self-hosted subset (src/lib/styles/material-symbols.css).
	const navGlyphByHref: Record<string, string> = {
		'/': 'space_dashboard',
		'/onboarding': 'rocket_launch',
		'/scan': 'document_scanner',
		'/accounting': 'account_balance_wallet',
		'/deposits': 'savings',
		'/reports': 'summarize',
		'/properties': 'apartment',
		'/units': 'home',
		'/tenants': 'group',
		'/leases': 'description',
		'/lease-templates': 'upload_file',
		'/applications': 'assignment',
		'/maintenance': 'build',
		'/appointments': 'event',
		'/vendors': 'contacts',
		'/messages': 'forum',
		'/notices': 'campaign',
		'/ai': 'auto_awesome',
		'/docs': 'menu_book',
		'/settings': 'settings',
		'/admin/users': 'shield',
		'/owners': 'account_balance',
		'/audit': 'history'
	};
	// Material Symbols glyph per staff nav group id (collapsible section headers).
	const navGlyphByGroup: Record<string, string> = {
		money: 'account_balance_wallet',
		rentals: 'work',
		work: 'build',
		inbox: 'forum',
		settings: 'settings'
	};

	// Identity is sourced from the SERVER layout data (page.data.user), which is populated during
	// SSR by every group layout's +layout.server.ts (root + protected/portal/admin/superadmin all
	// return `user: locals.user`). Reading the client auth store here instead would be null on the
	// server (the store seeds inside a $effect, which doesn't run during SSR), so the nav/account
	// menu would render logged-out and then "pop" to authenticated after hydration — a visible
	// wrong-state flash plus server/client markup divergence (M-15). The client store stays the
	// source for outgoing Authorization headers and post-login mutations, not for shell rendering.
	let currentUser = $derived(page.data.user ?? null);
	let portalUser = $derived(isPortalUser(currentUser) && !isStaff(currentUser));
	const userSecurityHref = $derived(portalUser ? '/portal/security' : '/settings/security');

	const portalNavItems: NavItem[] = [
		{ href: '/portal', label: 'Dashboard', icon: Home },
		{ href: '/portal/messages', label: 'Messages', icon: MessageSquare },
		{ href: '/portal/notifications', label: 'Notifications', icon: BellRing },
		{ href: '/portal/maintenance', label: 'Maintenance', icon: Wrench },
		{ href: '/portal/payments', label: 'Payments', icon: CreditCard },
		{ href: '/portal/lease', label: 'Lease', icon: FileText },
		{ href: '/portal/appointments', label: 'Appointments', icon: Calendar }
	];

	const portalUtilityItems: NavItem[] = [
		{ href: '/portal/security', label: 'Security', icon: Shield }
	];
	const commandCenterTitleItem: NavItem = { href: '/units/', label: 'Command Center', icon: Home };

	function itemVisible(item: NavItem): boolean {
		if (!item.roles || item.roles.length === 0) return true;
		return hasRole(currentUser, ...item.roles);
	}

	// Staff groups filtered to the items the current user may see; empty groups dropped.
	let visibleGroups = $derived.by(() =>
		staffNavGroups
			.map((g) => ({ ...g, items: g.items.filter(itemVisible) }))
			.filter((g) => g.items.length > 0)
	);

	// Pinned single links (Dashboard, Scan / Edit) above the groups.
	let visiblePinned = $derived.by(() => pinnedNavItems.filter(itemVisible));

	// Command Center (the per-unit drill-down) is surfaced as its own pinned entry below Scan / Edit;
	// staff-only, gated to the same roles as the Units nav item.
	let canSeeCommandCenter = $derived(hasRole(currentUser, 'Admin', 'Manager', 'Agent'));

	// Bottom-rail standalone links (Assistant, Help).
	let visibleBottomRail = $derived.by(() => bottomRailItems.filter(itemVisible));

	// Settings hub group filtered to the current user's items (empty → hidden).
	let visibleSettingsGroup = $derived.by(() => {
		const items = settingsGroup.items.filter(itemVisible);
		return items.length > 0 ? { ...settingsGroup, items } : null;
	});

	// Flat list of every visible nav item (both modes) for title resolution.
	let allItems = $derived.by(() =>
		portalUser
			? [...portalNavItems, ...portalUtilityItems]
				: [
						...visiblePinned,
						...(canSeeCommandCenter ? [commandCenterTitleItem] : []),
						...visibleGroups.flatMap((g) => g.items),
						...visibleBottomRail,
						...(visibleSettingsGroup?.items ?? [])
				]
	);

	function isActive(href: string): boolean {
		const currentPath = page.url.pathname;
		if (href === '/') return currentPath === '/';
		if (href === '/units') return currentPath === '/units';
		return currentPath.startsWith(href);
	}

	function groupHasActive(group: NavGroup): boolean {
		return group.items.some((i) => isActive(i.href));
	}

	function commandCenterHasActive(): boolean {
		return page.url.pathname.startsWith('/units/');
	}

	// --- Collapsible group open/closed state (remembered) -----------------------
	// Versioned (…v2) because IA Wave 1 changed the group ids (overview/portfolio/operations/
	// directory/admin → money/rentals/work/inbox/settings); a stale v1 blob would carry dead ids.
	const STORAGE_KEY = 'rc.nav.groups.v2';
	let openGroups = $state<Record<string, boolean>>({});

	// One-time cleanup of the pre-Wave-1 key so it doesn't linger in users' storage.
	$effect(() => {
		if (!browser) return;
		try {
			localStorage.removeItem('rc.nav.groups');
		} catch {
			/* ignore */
		}
	});

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

	// Accordion default: groups start CLOSED; only the group containing the active route auto-opens
	// (and, to keep the one-open-at-a-time invariant, its siblings are forced closed). No group is
	// defaulted to open. Reads openGroups untracked (it WRITES openGroups, and should only re-run
	// when the route / visible groups change — not on its own write).
	$effect(() => {
		const current = untrack(() => openGroups);
		const next = { ...current };
		let changed = false;
		const groupsForState = visibleSettingsGroup ? [...visibleGroups, visibleSettingsGroup] : visibleGroups;
		const groupIdsForState = [
			...groupsForState.map((g) => g.id),
			...(canSeeCommandCenter ? ['command-center'] : [])
		];
		const activeGroupId =
			canSeeCommandCenter && commandCenterHasActive()
				? 'command-center'
				: groupsForState.find((g) => groupHasActive(g))?.id;
		if (activeGroupId) {
			for (const id of groupIdsForState) {
				const shouldOpen = id === activeGroupId;
				if ((next[id] ?? false) !== shouldOpen) {
					next[id] = shouldOpen;
					changed = true;
				}
			}
		}
		if (changed) openGroups = next;
	});

	// Accordion: only one group open at a time. Opening a group closes every other group; clicking
	// the open group's header just closes it. Persisted so the choice survives reloads.
	function writeOpenGroups(next: Record<string, boolean>) {
		openGroups = next;
		if (browser) {
			try {
				localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
			} catch {
				/* storage may be unavailable */
			}
		}
	}

	function toggleGroup(id: string) {
		const willOpen = !openGroups[id];
		const next: Record<string, boolean> = {};
		for (const key of Object.keys(openGroups)) next[key] = false;
		next[id] = willOpen;
		writeOpenGroups(next);
	}

	function openOnlyGroup(id: string) {
		const next: Record<string, boolean> = {};
		for (const key of Object.keys(openGroups)) next[key] = false;
		next[id] = true;
		writeOpenGroups(next);
	}

	function setGroupOpen(id: string, open: boolean) {
		if (open) {
			openOnlyGroup(id);
			return;
		}
		writeOpenGroups({ ...openGroups, [id]: false });
	}

	function expandCollapsedGroup(id: string) {
		sidebarCollapsed = false;
		openOnlyGroup(id);
	}

	function navTestId(href: string): string {
		const path = href.split('?')[0].replace(/^\/+/, '').replaceAll('/', '-');
		return `nav-${path || 'dashboard'}`;
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

	// Sign out: tear down the client mirror (which also fires the SignalR-disconnect callback), then
	// hand off to the server /logout endpoint with a FULL-DOCUMENT navigation. A client-side goto can
	// race the httpOnly Set-Cookie deletions, leaving a still-valid access-token cookie behind — so the
	// next visit to /login sees locals.user and auto-resumes the dead session. A top-level navigation
	// makes the browser apply the /logout cookie-deletion headers and follow its 303 → /login as a fresh
	// request with no session cookies, so the session is actually dead (no auto-resume).
	function signOut() {
		clearAuthState();
		if (browser) {
			window.location.href = '/logout';
		}
	}

	// --- Header quick-action live counts ---------------------------------------
	// Gate the live-count queries on the server-sourced identity too, so they don't fire a
	// 401-bound request during the brief pre-hydration window. `enabled` is reactive (createQuery
	// takes a thunk), so it flips on once page.data.user is present.
	const isStaffSession = $derived(hasRole(currentUser, 'Admin', 'Manager', 'Agent'));
	const showStaffHeader = $derived(!portalUser);

	const unreadMessagesQuery = createQuery(() => ({
		queryKey: ['header-unread-messages'],
		enabled: isStaffSession && !portalUser,
		queryFn: () => messagesApi.unreadCount(),
		staleTime: 30_000,
		refetchInterval: 60_000
	}));
	let unreadMessages = $derived(unreadMessagesQuery.data?.count ?? 0);

	const upcomingApptsQuery = createQuery(() => ({
		queryKey: ['header-upcoming-appointments', getCurrentPortfolioId()],
		enabled: isStaffSession && !portalUser,
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
<M3TooltipLayer data-testid="shell-m3-tooltip" />

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

<!-- A single expanded nav link (used for pinned items, group items, and bottom-rail links). -->
{#snippet navLink(item: NavItem)}
	{@const active = isActive(item.href)}
	{@const glyph = navGlyphByHref[item.href]}
	<M3NavItem
		href={item.href}
		label={item.label}
		active={active}
		tone={active ? 'primary' : 'neutral'}
		variant={item.href === '/' || item.href === '/scan' ? 'surface' : 'plain'}
		class={item.href === '/' || item.href === '/scan' ? 'mb-2' : 'min-h-9 py-1.5'}
		onclick={handleNavClick}
		data-testid={navTestId(item.href)}
	>
		{#snippet icon()}
			{#if glyph}
				<MaterialSymbol name={glyph} size={20} data-testid="{navTestId(item.href)}-icon" />
			{:else}
				<item.icon class="h-4 w-4 shrink-0" data-testid="{navTestId(item.href)}-icon" />
			{/if}
		{/snippet}
	</M3NavItem>
{/snippet}

<!-- A single collapsed-rail nav link (icon only, tooltip on hover). -->
{#snippet navLinkCollapsed(item: NavItem)}
	{@const active = isActive(item.href)}
	{@const glyph = navGlyphByHref[item.href]}
	<M3NavItem
		href={item.href}
		label={item.label}
		active={active}
		collapsed
		tone={active ? 'primary' : 'neutral'}
		variant={item.href === '/' || item.href === '/scan' ? 'surface' : 'plain'}
		class="mb-1"
		onclick={handleNavClick}
		data-testid={navTestId(item.href)}
	>
		{#snippet icon()}
			{#if glyph}
				<MaterialSymbol name={glyph} size={20} data-testid="{navTestId(item.href)}-icon" />
			{:else}
				<item.icon class="h-4 w-4 shrink-0" data-testid="{navTestId(item.href)}-icon" />
			{/if}
		{/snippet}
	</M3NavItem>
{/snippet}

<!-- An expanded collapsible group (header + its items). -->
{#snippet navGroup(group: NavGroup)}
	{@const open = openGroups[group.id] ?? false}
	{@const active = groupHasActive(group)}
	<div class="mb-0.5">
		<M3NavGroup
			label={group.label}
			active={active}
			expanded={open}
			onclick={() => toggleGroup(group.id)}
			data-testid="nav-group-{group.id}"
		>
			{#snippet icon()}
				{#if navGlyphByGroup[group.id]}
					<MaterialSymbol name={navGlyphByGroup[group.id]} size={20} data-testid="nav-group-{group.id}-icon" />
				{:else}
					<group.icon class="h-4 w-4 shrink-0" data-testid="nav-group-{group.id}-icon" />
				{/if}
			{/snippet}
		</M3NavGroup>
		{#if open}
			<div class="m3-motion-reveal-list mt-0.5 space-y-0.5 pl-3">
				{#each group.items as item}
					{@render navLink(item)}
				{/each}
			</div>
		{/if}
	</div>
{/snippet}

<!-- A collapsed group header mirrors EdiPlatform: icon-only group, expands rail on click. -->
{#snippet navGroupCollapsed(group: NavGroup)}
	<M3NavGroup
		label={group.label}
		active={groupHasActive(group)}
		collapsed
		class="mb-1"
		onclick={() => expandCollapsedGroup(group.id)}
		data-testid="nav-group-{group.id}"
	>
		{#snippet icon()}
			{#if navGlyphByGroup[group.id]}
				<MaterialSymbol name={navGlyphByGroup[group.id]} size={20} data-testid="nav-group-{group.id}-icon" />
			{:else}
				<group.icon class="h-4 w-4 shrink-0" data-testid="nav-group-{group.id}-icon" />
			{/if}
		{/snippet}
	</M3NavGroup>
{/snippet}

<div class="flex h-full w-full overflow-hidden bg-background">
	<!-- Sidebar -->
	<aside
		class="rc-vt-sidebar fixed left-0 top-0 z-40 flex h-full flex-col border-r border-sidebar-border bg-sidebar/95 shadow-[inset_-1px_0_0_rgb(255_255_255_/_0.025)] backdrop-blur transition-all duration-[var(--m3-motion-duration-medium-2)] ease-[var(--m3-motion-easing-emphasized-decelerate)]
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
					<span class="flex h-7 w-7 items-center justify-center rounded-[var(--m3-shape-large)] bg-primary/15 text-primary ring-1 ring-inset ring-primary/25">
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
						class="m3-state-layer flex items-center gap-2 rounded-[var(--m3-shape-full)] px-3 py-2 text-sm transition-colors
							{active
							? 'bg-sidebar-accent font-medium text-sidebar-accent-foreground shadow-[inset_0_0_0_1px_color-mix(in_srgb,var(--primary)_24%,transparent)]'
							: 'text-muted-foreground hover:bg-sidebar-accent hover:text-sidebar-foreground'}
							{sidebarCollapsed && !isMobile ? 'justify-center' : ''}"
						aria-label={sidebarCollapsed && !isMobile ? item.label : undefined}
						data-m3-tooltip={sidebarCollapsed && !isMobile ? item.label : undefined}
						data-testid="nav-{item.href.replace('/', '').replace('/', '-') || 'dashboard'}"
					>
						<item.icon class="h-4 w-4 shrink-0 {active ? 'text-primary' : ''}" />
						{#if !sidebarCollapsed || isMobile}
							<span class="truncate">{item.label}</span>
						{/if}
					</a>
				{/each}
			{:else if sidebarCollapsed && !isMobile}
				<!-- Collapsed rail: pinned links plus icon-only group headers, matching EdiPlatform. -->
				{#each visiblePinned as item}
					{@render navLinkCollapsed(item)}
				{/each}
				{#if canSeeCommandCenter}
					<CommandCenterNav
						collapsed
						open={openGroups['command-center'] ?? false}
						onOpenChange={(next) => setGroupOpen('command-center', next)}
						onNavigate={handleNavClick}
					/>
				{/if}
				{#each visibleGroups as group}
					{@render navGroupCollapsed(group)}
				{/each}
				<div class="my-2 border-t border-sidebar-border"></div>
				{#each visibleBottomRail as item}
					{@render navLinkCollapsed(item)}
				{/each}
				{#if visibleSettingsGroup}
					{@render navGroupCollapsed(visibleSettingsGroup)}
				{/if}
			{:else}
				<!-- Expanded: pinned links, then collapsible groups, then bottom rail -->
				{#each visiblePinned as item}
					{@render navLink(item)}
				{/each}
				{#if canSeeCommandCenter}
					<CommandCenterNav
						open={openGroups['command-center'] ?? false}
						onOpenChange={(next) => setGroupOpen('command-center', next)}
						onNavigate={handleNavClick}
					/>
				{/if}
				<div class="my-2"></div>
				{#each visibleGroups as group}
					{@render navGroup(group)}
				{/each}
				<div class="my-2 border-t border-sidebar-border"></div>
				{#each visibleBottomRail as item}
					{@render navLink(item)}
				{/each}
				{#if visibleSettingsGroup}
					{@render navGroup(visibleSettingsGroup)}
				{/if}
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
							<DropdownMenuItem data-testid="user-menu-guided-setup-collapsed">
								<a href="/onboarding?from=account-menu" class="flex w-full items-center gap-2">
									<ClipboardList class="h-4 w-4" />
									Guided Setup
								</a>
							</DropdownMenuItem>
							<DropdownMenuItem data-testid="user-menu-settings">
								<a href="/settings" class="flex w-full items-center gap-2">
									<Settings class="h-4 w-4" />
									Settings
								</a>
							</DropdownMenuItem>
						{/if}
						<DropdownMenuItem data-testid="user-menu-security-collapsed">
							<a href={userSecurityHref} class="flex w-full items-center gap-2">
								<Shield class="h-4 w-4" />
								Security
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
							<DropdownMenuItem data-testid="user-menu-guided-setup">
								<a href="/onboarding?from=account-menu" class="flex w-full items-center gap-2">
									<ClipboardList class="h-4 w-4" />
									Guided Setup
								</a>
							</DropdownMenuItem>
							<DropdownMenuItem data-testid="user-menu-settings">
								<a href="/settings" class="flex w-full items-center gap-2">
									<Settings class="h-4 w-4" />
									Settings
								</a>
							</DropdownMenuItem>
						{/if}
						<DropdownMenuItem data-testid="user-menu-security">
							<a href={userSecurityHref} class="flex w-full items-center gap-2">
								<Shield class="h-4 w-4" />
								Security
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
					class="m3-state-layer mt-1 flex w-full items-center gap-2 rounded-[var(--m3-shape-full)] px-3 py-2 text-sm text-muted-foreground transition-colors hover:bg-sidebar-accent hover:text-sidebar-foreground {sidebarCollapsed ? 'justify-center' : ''}"
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
		class="flex h-full min-w-0 flex-1 flex-col transition-all duration-[var(--m3-motion-duration-medium-2)] ease-[var(--m3-motion-easing-emphasized-decelerate)] {isMobile
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
			class="rc-vt-topbar sticky top-0 z-30 flex h-14 items-center gap-2 border-b border-border bg-background/88 px-3 shadow-[inset_0_-1px_0_rgb(255_255_255_/_0.025)] backdrop-blur supports-[backdrop-filter]:bg-background/70 sm:px-4"
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
					<!-- Scan / Edit -->
					<ScanLauncher
						triggerLabel=""
						ariaLabel="Scan / Edit"
						tooltip="Scan / Edit"
						testid="header-scan"
						triggerVariant="ghost"
						triggerClass="m3-state-layer relative size-9 p-0 text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
					/>

					<!-- Messages -->
					<a
						href="/messages"
						class="m3-state-layer relative flex items-center justify-center rounded-[var(--m3-shape-full)] p-2 text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
						aria-label="Messages{unreadMessages > 0 ? ` (${unreadMessages} unread)` : ''}"
						data-m3-tooltip="Messages"
						data-testid="header-messages"
					>
						<MessageSquare class="h-5 w-5" />
						{@render countBadge(unreadMessages)}
					</a>

					<!-- Appointments -->
					<a
						href="/appointments"
						class="m3-state-layer relative flex items-center justify-center rounded-[var(--m3-shape-full)] p-2 text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
						aria-label="Appointments{upcomingAppts > 0 ? ` (${upcomingAppts} upcoming)` : ''}"
						data-m3-tooltip="Upcoming appointments"
						data-testid="header-appointments"
					>
						<Calendar class="h-5 w-5" />
						{@render countBadge(upcomingAppts)}
					</a>
				{/if}

				<!-- Help & Docs -->
				<a
					href="/docs"
					class="m3-state-layer relative flex items-center justify-center rounded-[var(--m3-shape-full)] p-2 text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
					aria-label="Help and documentation"
					data-m3-tooltip="Help & Docs"
					data-testid="header-help"
				>
					<HelpCircle class="h-5 w-5" />
				</a>

				<!-- Theme -->
				<ThemeModeToggle data-testid="header-theme-toggle" />

				<!-- Notifications -->
				<NotificationBell data-testid="notification-bell-header" placement="down" />
			</div>
		</header>

		<!-- Page content frame. Routes own their internal 100% scroll area. -->
		<main class="customer-shell-main flex min-h-0 flex-1 justify-center overflow-hidden">
			<div class="h-full w-full max-w-[1600px]">
				{#key page.url.pathname}
					<div class="m3-route-transition" data-testid="route-transition-frame">
						{@render children()}
					</div>
				{/key}
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
