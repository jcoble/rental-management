<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { ArrowLeft } from '@lucide/svelte';
	import { team } from '$lib/api/endpoints/team';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { canAccessPathForEnvelope, safeLandingForAccess } from '$lib/auth/experience-policy';
	import { notificationSections } from '$lib/notifications/notification-sections';
	import MyAlertsSection from '$lib/components/notifications/MyAlertsSection.svelte';
	import TenantNoticesSection from '$lib/components/notifications/TenantNoticesSection.svelte';
	import TeamRoutingSection from '$lib/components/notifications/TeamRoutingSection.svelte';

	const authState = getAuthState();
	const returnHref = $derived.by(() => {
		const access = authState.accessEnvelope;
		if (!access) return '/';
		return canAccessPathForEnvelope(access, '/settings')
			? '/settings'
			: (safeLandingForAccess(access) ?? '/');
	});
	const canSeeTenantNotices = $derived.by(() => {
		const access = authState.accessEnvelope;
		return !!access && canAccessPathForEnvelope(access, '/settings/notifications/tenant-notices');
	});
	const canSeeTeamRouting = $derived.by(() => {
		const access = authState.accessEnvelope;
		return !!access && canAccessPathForEnvelope(access, '/settings/notifications/team-routing');
	});

	// "Who gets told what" is only meaningful once somebody else works here, so ask for a single
	// team member and read the total.
	const teamCountQuery = createQuery(() => ({
		queryKey: ['team-members', 'notification-sections'],
		queryFn: () => team.members({ skip: 0, take: 1 }),
		enabled: canSeeTeamRouting
	}));
	const hasTeamMembers = $derived((teamCountQuery.data?.totalCount ?? 0) > 1);
	const sections = $derived(notificationSections({ hasTeamMembers }));
</script>

<svelte:head><title>Notifications · Rental Command</title></svelte:head>

<div
	class="mx-auto box-border h-full w-full max-w-5xl space-y-6 overflow-y-auto p-4 pb-20 sm:p-6"
	data-testid="notifications-settings-page"
>
	<header class="space-y-3">
		<a
			href={returnHref}
			class="inline-flex min-h-11 items-center gap-2 text-sm text-muted-foreground hover:text-foreground"
		>
			<ArrowLeft class="size-4" /> Back to settings
		</a>
		<div class="max-w-3xl">
			<h1 class="text-2xl font-semibold tracking-tight">Notifications</h1>
			<p class="mt-2 text-sm leading-6 text-muted-foreground">
				Everything Rental Command sends: your own alerts, the messages tenants get, and who on your
				team hears about what.
			</p>
		</div>
	</header>

	<MyAlertsSection />

	{#if canSeeTenantNotices}
		<TenantNoticesSection />
	{/if}

	{#if canSeeTeamRouting}
		{#if sections.includes('team-routing')}
			<TeamRoutingSection />
		{:else}
			<section id="team-routing" class="scroll-mt-6" data-testid="notifications-team-routing-empty">
				<h2 class="text-lg font-semibold tracking-tight">Who gets told what</h2>
				<p class="mt-1 text-sm text-muted-foreground">Appears when you add a team member.</p>
			</section>
		{/if}
	{/if}
</div>
