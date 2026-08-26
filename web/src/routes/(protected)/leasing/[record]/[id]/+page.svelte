<script lang="ts">
	import { page } from '$app/state';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { ArrowLeft, CalendarDays, FileSignature, Home, Inbox, Send, UserRound } from '@lucide/svelte';
	import { leasingWorkspace } from '$lib/api/endpoints/leasing-workspace';
	import { CAPABILITY } from '$lib/auth/experience-policy';
	import ListingTab from '$lib/components/unit/tabs/ListingTab.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import { Button } from '$lib/components/ui/button';
	import type { WorkspaceExperience } from '$lib/types/user';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { formatStatusLabel } from '$lib/utils/status-labels';
	import { formatAccountingCurrency } from '$lib/accounting/accounting-display';

	type LeasingRecord = 'rentals' | 'applications' | 'appointments' | 'conversations' | 'move-ins';

	const queryClient = useQueryClient();
	const record = $derived(page.params.record as LeasingRecord);
	const id = $derived(Number(page.params.id));
	let replyBody = $state('');

	const meta = $derived.by(() => ({
		rentals: { label: 'Rental & listing', back: '/leasing/rentals', icon: Home },
		applications: { label: 'Application', back: '/leasing/pipeline', icon: UserRound },
		appointments: { label: 'Showing', back: '/leasing/calendar', icon: CalendarDays },
		conversations: { label: 'Conversation', back: '/leasing/inbox', icon: Inbox },
		'move-ins': { label: 'Move-in', back: '/leasing/pipeline', icon: Home }
	})[record]);

	const detailQuery = createQuery(() => ({
		queryKey: ['leasing-workspace', 'detail', record, id],
		queryFn: async () => {
			if (record === 'rentals') return await leasingWorkspace.rental(id);
			if (record === 'applications') return await leasingWorkspace.application(id);
			if (record === 'appointments') return await leasingWorkspace.appointment(id);
			if (record === 'conversations') return await leasingWorkspace.conversation(id);
			if (record === 'move-ins') return await leasingWorkspace.moveIn(id);
			throw new Error('Unknown leasing record type.');
		},
		enabled: Number.isInteger(id) && id > 0 && !!meta
	}));

	const detail = $derived(detailQuery.data as Record<string, any> | undefined);
	const activeExperience = $derived(page.data.access?.selectedContext.activeExperience ?? null);
	const activeCapabilities = $derived(new Set(
		page.data.access?.navigation.find((entry: { experience: WorkspaceExperience; capabilityKeys: string[] }) =>
			entry.experience === activeExperience
		)?.capabilityKeys ?? []
	));
	const canPrepareAgreements = $derived(
		activeExperience === 'Leasing' && activeCapabilities.has(CAPABILITY.leasingAgreementsPrepare)
	);

	const replyMutation = createMutation(() => ({
		mutationFn: () => leasingWorkspace.reply(id, replyBody.trim()),
		onSuccess: async () => {
			replyBody = '';
			await queryClient.invalidateQueries({ queryKey: ['leasing-workspace', 'detail', 'conversations', id] });
			await queryClient.invalidateQueries({ queryKey: ['leasing-workspace', 'inbox'] });
			showSuccess('Reply sent.');
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));

	function date(value?: string | null): string {
		return value
			? new Intl.DateTimeFormat('en-US', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value))
			: 'Not scheduled';
	}

	function money(value?: number | null): string {
		return value == null ? 'Not set' : formatAccountingCurrency(value);
	}
</script>

<svelte:head><title>{meta?.label ?? 'Leasing record'} | Rental Command</title></svelte:head>

<section class="h-full overflow-y-auto" data-testid="leasing-record-detail">
	<div class="mx-auto max-w-4xl space-y-6 px-6 py-8">
		{#if meta}
			<a href={meta.back} class="inline-flex min-h-11 items-center gap-2 text-sm text-muted-foreground hover:text-foreground">
				<ArrowLeft class="h-4 w-4" /> Back to {meta.label === 'Conversation' ? 'Inbox' : meta.label === 'Showing' ? 'Calendar' : 'Leasing'}
			</a>
		{/if}

		{#if detailQuery.isLoading}
			<LoadingState label="Loading leasing record" variant="page" testid="leasing-record-loading" />
		{:else if detailQuery.isError || !detail || !meta}
			<div class="rounded-xl border border-destructive/40 p-8 text-center">
				<h1 class="font-semibold">This leasing record is unavailable</h1>
				<p class="mt-2 text-sm text-muted-foreground">It may be outside your assigned properties or no longer active.</p>
				{#if detailQuery.isError}
					<Button type="button" variant="outline" size="sm" class="mt-3" onclick={() => detailQuery.refetch()}>Try again</Button>
				{/if}
			</div>
		{:else}
			<header class="flex items-start gap-4">
				<div class="rounded-xl bg-primary/10 p-3 text-primary"><meta.icon class="h-6 w-6" /></div>
				<div>
					<p class="text-sm font-medium text-primary">Leasing</p>
					<h1 class="text-3xl font-semibold tracking-tight">
						{record === 'rentals' ? `${detail.propertyName} · Unit ${detail.unitNumber}` : record === 'applications' ? detail.applicantName : record === 'appointments' ? detail.title : record === 'conversations' ? detail.subject : detail.tenantName}
					</h1>
					<p class="mt-1 text-muted-foreground">{meta.label}</p>
				</div>
			</header>

			{#if record === 'rentals'}
				<div class="grid gap-4 md:grid-cols-2">
					<div class="rounded-xl border bg-card p-5">
						<h2 class="font-semibold">Rental</h2>
						<p class="mt-2 text-sm text-muted-foreground">{detail.address}</p>
						{#if detail.canViewApplications}<p class="mt-4 text-sm">{detail.openApplicationCount} open applications</p>{/if}
						{#if detail.canViewShowings}<p class="mt-4 text-sm">Next showing: {date(detail.nextShowingAtUtc)}</p>{/if}
						{#if canPrepareAgreements && detail.leaseManagementId}
							<Button href={`/leases/${detail.leaseManagementId}`} variant="outline" size="sm" class="mt-4 gap-2" data-testid="leasing-rental-open-agreement-workspace">
								<FileSignature class="h-4 w-4" /> Open agreement workspace
							</Button>
						{/if}
					</div>
					<div class="rounded-xl border bg-card p-5"><h2 class="font-semibold">Current listing</h2><p class="mt-2 text-lg font-medium">{detail.listingHeadline || 'No listing started'}</p><p class="text-sm text-muted-foreground">{detail.listingStatus || 'Not listed'} · {money(detail.askingRent)}</p><p class="mt-3 text-sm">Available {date(detail.availableOn)}</p><p class="text-sm">Deposit {money(detail.securityDeposit)}</p></div>
				</div>
				{#if detail.listingDescription || detail.leaseTerms || detail.petPolicy}<div class="rounded-xl border bg-card p-5"><h2 class="font-semibold">Listing copy & terms</h2>{#if detail.listingDescription}<p class="mt-3 whitespace-pre-wrap text-sm">{detail.listingDescription}</p>{/if}{#if detail.leaseTerms}<p class="mt-3 text-sm"><span class="font-medium">Lease terms:</span> {detail.leaseTerms}</p>{/if}{#if detail.petPolicy}<p class="mt-2 text-sm"><span class="font-medium">Pet policy:</span> {detail.petPolicy}</p>{/if}</div>{/if}
				<ListingTab unitId={detail.unitId} />
			{:else if record === 'applications'}
				<div class="grid gap-4 md:grid-cols-2"><div class="rounded-xl border bg-card p-5"><h2 class="font-semibold">Applicant</h2><p class="mt-3 text-sm">{detail.email || 'No email'}</p><p class="text-sm">{detail.phone || 'No phone'}</p><p class="mt-3 text-sm">Submitted {date(detail.submittedAtUtc)}</p></div><div class="rounded-xl border bg-card p-5"><h2 class="font-semibold">Application</h2><p class="mt-3 text-sm">{detail.propertyName || 'No property'}{detail.unitNumber ? ` · Unit ${detail.unitNumber}` : ''}</p><p class="text-sm">Status: {formatStatusLabel(detail.status)}</p><p class="text-sm">Desired move-in: {date(detail.desiredMoveInDate)}</p><p class="text-sm">Monthly income: {money(detail.monthlyIncome)}</p><p class="text-sm">Screening consent: {detail.consentGiven ? 'Captured' : 'Not captured'}</p></div></div>{#if detail.notes}<div class="rounded-xl border bg-card p-5"><h2 class="font-semibold">Notes</h2><p class="mt-3 whitespace-pre-wrap text-sm">{detail.notes}</p></div>{/if}
			{:else if record === 'appointments'}
				<div class="grid gap-4 md:grid-cols-2"><div class="rounded-xl border bg-card p-5"><h2 class="font-semibold">Schedule</h2><p class="mt-3 text-sm">{date(detail.scheduledStart)}</p>{#if detail.scheduledEnd}<p class="text-sm">Ends {date(detail.scheduledEnd)}</p>{/if}<p class="mt-2 text-sm">Status: {formatStatusLabel(detail.status)}</p></div><div class="rounded-xl border bg-card p-5"><h2 class="font-semibold">Prospect & rental</h2><p class="mt-3 text-sm">{detail.prospectName || 'Prospect'}</p><p class="text-sm">{detail.prospectEmail || 'No email'}</p><p class="mt-2 text-sm">{detail.propertyName || 'No property'}{detail.unitNumber ? ` · Unit ${detail.unitNumber}` : ''}</p>{#if detail.assignedTo}<p class="mt-2 text-sm">Assigned to {detail.assignedTo}</p>{/if}</div></div>{#if detail.notes}<div class="rounded-xl border bg-card p-5"><h2 class="font-semibold">Showing notes</h2><p class="mt-3 whitespace-pre-wrap text-sm">{detail.notes}</p></div>{/if}
			{:else if record === 'conversations'}
				<div class="rounded-xl border bg-card p-5"><p class="text-sm text-muted-foreground">{detail.tenantName}{detail.propertyName ? ` · ${detail.propertyName}` : ''}</p><div class="mt-5 space-y-3">{#each detail.messages as message (message.id)}<div class={`max-w-[85%] rounded-xl p-3 text-sm ${message.senderRole === 'Landlord' ? 'ml-auto bg-primary text-primary-foreground' : 'bg-muted'}`}><p class="whitespace-pre-wrap">{message.body}</p><p class="mt-1 text-xs opacity-70">{date(message.createdAt)}</p></div>{/each}</div><form class="mt-5 flex gap-3" onsubmit={(event) => { event.preventDefault(); if (replyBody.trim()) replyMutation.mutate(); }}><textarea bind:value={replyBody} class="min-h-24 flex-1 rounded-md border border-input bg-background px-3 py-2 text-sm" maxlength="4000" placeholder="Reply in the tenant portal" aria-label="Reply"></textarea><Button type="submit" disabled={!replyBody.trim() || replyMutation.isPending}><Send class="h-4 w-4" /> Send</Button></form></div>
			{:else}
				<div class="grid gap-4 md:grid-cols-2">
					<div class="rounded-xl border bg-card p-5">
						<h2 class="font-semibold">Household</h2>
						<p class="mt-3 text-sm">{detail.tenantName}</p>
						<p class="text-sm">{detail.propertyName} · Unit {detail.unitNumber}</p>
						<p class="mt-2 text-xs text-muted-foreground">{detail.relationshipNumber}</p>
						{#if canPrepareAgreements}
							<Button href={`/leases/${detail.id}`} variant="outline" size="sm" class="mt-4 gap-2" data-testid="leasing-move-in-open-agreement-workspace">
								<FileSignature class="h-4 w-4" /> Open agreement workspace
							</Button>
						{/if}
					</div>
					<div class="rounded-xl border bg-card p-5">
						<h2 class="font-semibold">Move-in readiness</h2>
						<p class="mt-3 text-sm">Keys handed over: {date(detail.plannedPossessionAtUtc)}</p>
						<p class="text-sm">Agreement: {detail.agreementFullyExecuted ? 'Fully executed' : 'Still needs execution'}</p>
						<p class="text-sm">Possession: {detail.possessionGiven ? 'Given' : 'Not yet given'}</p>
					</div>
				</div>
			{/if}
		{/if}
	</div>
</section>
