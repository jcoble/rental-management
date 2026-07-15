<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { page } from '$app/state';
	import { CalendarDays, ClipboardList, Home, Inbox, ScanLine } from '@lucide/svelte';
	import { leasingWorkspace } from '$lib/api/endpoints/leasing-workspace';
	import { CAPABILITY } from '$lib/auth/experience-policy';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { capabilityKeysForExperience } from '$lib/types/user';

	const todayQuery = createQuery(() => ({ queryKey: ['leasing-workspace', 'today'], queryFn: () => leasingWorkspace.today() }));
	const today = $derived(todayQuery.data);
	const authState = getAuthState();
	const currentAccess = $derived(page.data.access ?? authState.accessEnvelope);
	const currentExperience = $derived(authState.activeExperience ?? currentAccess?.selectedContext.activeExperience ?? null);
	const activeCapabilities = $derived(capabilityKeysForExperience(currentAccess, currentExperience));
	const canManageApplications = $derived(activeCapabilities.has(CAPABILITY.leasingApplicationsManage));
	const canManageListings = $derived(activeCapabilities.has(CAPABILITY.leasingListingsManage));
	const canManageShowings = $derived(activeCapabilities.has(CAPABILITY.leasingShowingsManage));
	const canManageOnboarding = $derived(activeCapabilities.has(CAPABILITY.leasingOnboardingManage));
	const canPrepareAgreements = $derived(activeCapabilities.has(CAPABILITY.leasingAgreementsPrepare));
	const canScan = $derived(canManageApplications || canPrepareAgreements);
	const metrics = $derived(today ? [
		...(canManageApplications ? [{ label: 'Applications to review', value: today.applicationsToReview, href: '/leasing/pipeline', icon: ClipboardList }] : []),
		...(canManageListings ? [{ label: 'Listings need attention', value: today.listingsNeedingAttention, href: '/leasing/rentals', icon: Home }] : []),
		...(canManageShowings ? [{ label: 'Showings today', value: today.showingsToday, href: '/leasing/calendar', icon: CalendarDays }] : []),
		...(canManageOnboarding ? [
			{ label: 'Upcoming move-ins', value: today.upcomingMoveIns, href: '/leasing/pipeline', icon: Home },
			{ label: 'Unread conversations', value: today.unreadConversations, href: '/leasing/inbox', icon: Inbox }
		] : [])
	] : []);
</script>

<svelte:head><title>Leasing today | Rental Command</title></svelte:head>
<section class="h-full overflow-y-auto" data-testid="leasing-today-page"><div class="mx-auto max-w-6xl space-y-7 px-6 py-8">
	<header><p class="text-sm font-medium text-primary">Leasing</p><h1 class="text-3xl font-semibold tracking-tight">Today</h1><p class="mt-1 max-w-2xl text-muted-foreground">Your assigned leasing work, in the order it needs attention.</p></header>
	{#if todayQuery.isLoading}<p class="rounded-xl border p-8 text-center text-muted-foreground">Loading today’s work…</p>{:else if todayQuery.isError || !today}<p class="rounded-xl border border-destructive/40 p-8 text-center text-destructive">We could not load your leasing work.</p>{:else if metrics.length === 0}<p class="rounded-xl border p-8 text-center text-muted-foreground">No leasing work areas are assigned to this role.</p>{:else}<div class="grid gap-4 sm:grid-cols-2 xl:grid-cols-5">{#each metrics as metric}<a href={metric.href} class="rounded-xl border bg-card p-4 transition-colors hover:border-primary/40 hover:bg-accent/30"><metric.icon class="h-5 w-5 text-primary" /><p class="mt-4 text-3xl font-semibold">{metric.value}</p><p class="mt-1 text-sm text-muted-foreground">{metric.label}</p></a>{/each}</div>{/if}
	{#if canScan}<div class="rounded-xl border bg-card p-5"><div class="flex items-start gap-4"><div class="rounded-lg bg-primary/10 p-2 text-primary"><ScanLine class="h-5 w-5" /></div><div><h2 class="font-semibold">Paper or PDF in hand?</h2><p class="mt-1 text-sm text-muted-foreground">Scan an application or signed lease. Rental Command extracts the fields into a draft for you to verify before anything is saved.</p><a href="/scan" class="mt-3 inline-block text-sm font-medium text-primary hover:underline">Scan a document</a></div></div></div>{/if}
</div></section>
