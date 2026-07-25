<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { notifications } from '$lib/api/endpoints/notifications';
	import { team, type TeamMemberSummary } from '$lib/api/endpoints/team';
	import type { TeamRoutingRecipientPreview, TeamRoutingRuleResponse, TeamRoutingTopic } from '$lib/api/types/notification';
	import { hasCapability } from '$lib/stores/auth.svelte';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { canAccessPathForEnvelope, safeLandingForAccess } from '$lib/auth/experience-policy';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import { Input } from '$lib/components/ui/input';
	import * as Card from '$lib/components/ui/card';
	import NotificationHelpAction from '$lib/components/notifications/NotificationHelpAction.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import { ArrowLeft, Search, X } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const authState = getAuthState();
	const returnHref = $derived.by(() => {
		const access = authState.accessEnvelope;
		if (!access) return '/';
		return canAccessPathForEnvelope(access, '/settings')
			? '/settings#notifications'
			: (safeLandingForAccess(access) ?? '/');
	});
	const canManage = $derived(hasCapability('notifications.manage'));
	const topics: Array<{ topic: TeamRoutingTopic; label: string; detail: string }> = [
		{ topic: 'RentAndMoney', label: 'Rent and money', detail: 'Rent, balances, payment exceptions, and account work.' },
		{ topic: 'ApplicationsAndLeasing', label: 'Applications and leasing', detail: 'Applications, screening, offers, and lease setup.' },
		{ topic: 'WorkOrders', label: 'Work orders', detail: 'New requests, assignments, scheduling, and escalations.' },
		{ topic: 'OwnerStatementsAndDecisions', label: 'Owner statements and decisions', detail: 'Owner reports, approvals, and decisions that need follow-up.' },
		{ topic: 'AccountAndSecurity', label: 'Account and security', detail: 'Access changes, security events, and workspace administration.' },
		{ topic: 'MorningBriefing', label: 'Morning Briefing', detail: 'The daily list of rent, lease, appointment, inspection, and urgent work that needs attention.' }
	];

	const rulesQuery = createQuery(() => ({
		queryKey: ['notification-settings', 'team-routing'],
		queryFn: () => notifications.teamRouting.list(),
		enabled: canManage
	}));
	const briefingQuery = createQuery(() => ({
		queryKey: ['notification-settings', 'morning-briefing'],
		queryFn: () => notifications.morningBriefing.get(),
		enabled: canManage
	}));

	let briefingForm = $state({ enabled: true, sendHourLocal: 8, includeEmpty: false });
	const briefingHourIsValid = $derived(
		Number.isInteger(briefingForm.sendHourLocal) &&
			briefingForm.sendHourLocal >= 0 && briefingForm.sendHourLocal <= 23
	);
	let briefingApplied = $state(false);
	$effect(() => {
		if (!briefingQuery.data || briefingApplied) return;
		briefingForm = {
			enabled: briefingQuery.data.enabled,
			sendHourLocal: briefingQuery.data.sendHourLocal,
			includeEmpty: briefingQuery.data.includeEmpty
		};
		briefingApplied = true;
	});

	const saveBriefingMutation = createMutation(() => ({
		mutationFn: () => notifications.morningBriefing.update(briefingForm),
		onSuccess: (saved) => {
			briefingForm = {
				enabled: saved.enabled,
				sendHourLocal: saved.sendHourLocal,
				includeEmpty: saved.includeEmpty
			};
			queryClient.setQueryData(['notification-settings', 'morning-briefing'], saved);
			showSuccess('Morning Briefing schedule was saved.');
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));

	let editingTopic = $state<TeamRoutingTopic | null>(null);
	let editingRuleId = $state<number | null>(null);
	let editingPropertyId = $state<number | null>(null);
	let editingScope = $state('All properties');
	let useFallback = $state(true);
	let memberSearch = $state('');
	let selectedRecipients = $state<Record<number, { userId: number; displayName: string; email: string | null; reason: string }>>({});
	let appliedRecipientRuleId = $state<number | null>(null);

	const recipientsQuery = createQuery(() => ({
		queryKey: ['notification-settings', 'team-routing', editingRuleId, 'recipients'],
		queryFn: () => notifications.teamRouting.recipients(editingRuleId!),
		enabled: canManage && editingRuleId != null
	}));

	const previewQuery = createQuery(() => ({
		queryKey: ['notification-settings', 'team-routing', editingRuleId, 'preview'],
		queryFn: () => notifications.teamRouting.preview(editingRuleId!),
		enabled: canManage && editingRuleId != null
	}));

	const memberSearchQuery = createQuery(() => ({
		queryKey: ['team-members', 'notification-routing', memberSearch.trim()],
		queryFn: () => team.members({ skip: 0, take: 20, search: memberSearch.trim(), sort: 'name' }),
		enabled: canManage && editingTopic != null && memberSearch.trim().length >= 2
	}));

	$effect(() => {
		if (editingRuleId == null) return;
		const data = recipientsQuery.data;
		if (!data || appliedRecipientRuleId === editingRuleId) return;
		selectedRecipients = Object.fromEntries(data.map((recipient) => [recipient.userId, recipient]));
		appliedRecipientRuleId = editingRuleId;
	});

	function topicMeta(topic: TeamRoutingTopic) {
		return topics.find((item) => item.topic === topic)!;
	}

	function teamRecipientChannelSummary(recipient: TeamRoutingRecipientPreview): string {
		const channels: string[] = [];
		if (recipient.enableInApp) channels.push('In-app');
		if (recipient.enableMobilePush) channels.push('Mobile push');
		if (recipient.enableEmail) channels.push('Email');
		if (recipient.enableSms) channels.push('SMS');
		return channels.length > 0 ? channels.join(', ') : 'No enabled channels';
	}

	function beginEdit(topic: TeamRoutingTopic, rule?: TeamRoutingRuleResponse) {
		editingTopic = topic;
		editingRuleId = rule?.id ?? null;
		editingPropertyId = rule?.propertyId ?? null;
		editingScope = rule?.scope ?? 'All properties';
		useFallback = rule?.useWorkspaceAdministratorFallback ?? true;
		memberSearch = '';
		appliedRecipientRuleId = null;
		selectedRecipients = {};
		if (typeof document !== 'undefined') requestAnimationFrame(() => document.getElementById('team-routing-editor')?.scrollIntoView({ behavior: 'smooth', block: 'start' }));
	}

	function addRecipient(member: TeamMemberSummary) {
		if (member.accessStatus !== 'Active' || member.membershipStatus !== 'Active') return;
		const label = topicMeta(editingTopic!).label.toLowerCase();
		selectedRecipients[member.userId] = {
			userId: member.userId,
			displayName: member.displayName,
			email: member.email,
			reason: `${member.displayName} is the named ${label} contact.`
		};
	}

	function removeRecipient(userId: number) {
		const next = { ...selectedRecipients };
		delete next[userId];
		selectedRecipients = next;
	}

	const saveMutation = createMutation(() => ({
		mutationFn: () => notifications.teamRouting.replace({
			topic: editingTopic!,
			propertyId: editingPropertyId,
			useWorkspaceAdministratorFallback: useFallback,
			recipients: Object.values(selectedRecipients).map(({ userId, reason }) => ({ userId, reason }))
		}),
		onSuccess: (saved) => {
			editingRuleId = saved.id;
			queryClient.invalidateQueries({ queryKey: ['notification-settings', 'team-routing'] });
			queryClient.invalidateQueries({ queryKey: ['notification-settings', 'team-routing', saved.id] });
			showSuccess(`${topicMeta(saved.topic).label} routing was saved.`);
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));
</script>

<svelte:head><title>Team routing · Rental Command</title></svelte:head>

<div class="mx-auto max-w-5xl space-y-6" data-testid="team-routing-page">
	<div>
		<a href={returnHref} class="mb-3 inline-flex min-h-11 items-center gap-2 text-sm text-muted-foreground hover:text-foreground"><ArrowLeft class="size-4" /> Back</a>
		<div class="flex flex-wrap items-start justify-between gap-4">
			<div><h1 class="text-2xl font-semibold tracking-tight">Team routing</h1><p class="mt-1 text-sm text-muted-foreground">Choose which named team members are responsible for each topic and show why they receive it.</p></div>
				<NotificationHelpAction
					title="How Team routing works"
					description="Routing assigns internal responsibility without changing anyone's personal channels."
					guidance="Name the team members responsible for each topic and explain why. Their saved My alerts choices determine which of their own destinations are used."
				/>
		</div>
	</div>

	{#if !canManage}
		<Card.Root><Card.Content class="p-6"><h2 class="font-semibold">Administrator access required</h2><p class="mt-2 text-sm text-muted-foreground">You can manage your own alerts, but only a workspace administrator can change team responsibilities.</p></Card.Content></Card.Root>
	{:else if rulesQuery.isLoading}
		<LoadingState label="Loading saved routing rules" testid="team-routing-loading" />
	{:else if rulesQuery.isError}
		<Card.Root><Card.Content class="space-y-4 p-6"><p class="text-sm text-destructive">We couldn't load team routing.</p><Button variant="outline" onclick={() => rulesQuery.refetch()}>Retry</Button></Card.Content></Card.Root>
	{:else}
		<Card.Root class="gap-0 py-0" data-testid="morning-briefing-settings">
			<Card.Content class="space-y-5 p-5">
				<div><h2 class="font-semibold">Morning Briefing schedule</h2><p class="mt-1 text-sm text-muted-foreground">Automatically sends the routed team member a daily list of what needs attention. Delivery channels come from that person's My alerts.</p></div>
				{#if briefingQuery.isLoading}
					<LoadingState label="Loading Morning Briefing schedule" variant="spinner" testid="morning-briefing-loading" />
				{:else if briefingQuery.isError || !briefingQuery.data}
					<div class="flex items-center gap-3"><p class="text-sm text-destructive">We couldn't load the Morning Briefing schedule.</p><Button variant="outline" onclick={() => briefingQuery.refetch()}>Retry</Button></div>
				{:else}
					<div class="grid gap-4 md:grid-cols-3">
						<label class="flex min-h-20 items-start gap-3 rounded-lg border border-border p-4"><Checkbox checked={briefingForm.enabled} onCheckedChange={(value) => (briefingForm.enabled = value === true)} /><span><span class="block font-medium">Send every morning</span><span class="block text-sm text-muted-foreground">Turn off to pause scheduled briefings.</span></span></label>
						<label class="block rounded-lg border border-border p-4"><span class="text-sm font-medium">Local send hour</span><Input class="mt-2" type="number" min="0" max="23" bind:value={briefingForm.sendHourLocal} /><span class="mt-1 block text-xs text-muted-foreground">0–23 in {briefingQuery.data.timeZone}</span></label>
						<label class="flex min-h-20 items-start gap-3 rounded-lg border border-border p-4"><Checkbox checked={briefingForm.includeEmpty} onCheckedChange={(value) => (briefingForm.includeEmpty = value === true)} /><span><span class="block font-medium">Send all-clear days</span><span class="block text-sm text-muted-foreground">Also send when nothing currently needs attention.</span></span></label>
					</div>
					<Button onclick={() => saveBriefingMutation.mutate()} disabled={saveBriefingMutation.isPending || !briefingHourIsValid}>{saveBriefingMutation.isPending ? 'Saving…' : 'Save Morning Briefing'}</Button>
				{/if}
			</Card.Content>
		</Card.Root>

		{#if (rulesQuery.data?.length ?? 0) === 0}
			<Card.Root><Card.Content class="space-y-3 p-6"><h2 class="font-semibold">Routing defaults are unavailable</h2><p class="text-sm text-muted-foreground">Every new workspace receives one saved rule for each responsibility. Reload this page; if the rules are still missing, the workspace setup needs attention.</p><Button variant="outline" onclick={() => rulesQuery.refetch()}>Reload routing</Button></Card.Content></Card.Root>
		{:else}
		<div class="grid gap-4 md:grid-cols-2">
			{#each rulesQuery.data ?? [] as rule (rule.id)}
				{@const item = topicMeta(rule.topic)}
				<Card.Root class="gap-0 py-0">
					<Card.Content class="space-y-4 p-5">
						<div><h2 class="font-semibold">{item.label}</h2><p class="mt-1 text-sm text-muted-foreground">{item.detail}</p></div>
						<div class="rounded-md border border-border p-3">
							<div class="flex items-start justify-between gap-3"><div><p class="text-sm font-medium">{rule.scope}</p><p class="text-xs text-muted-foreground">{rule.namedRecipientSummary}</p></div><Button size="sm" variant="outline" onclick={() => beginEdit(item.topic, rule)}>Edit</Button></div>
							<p class="mt-2 text-xs text-muted-foreground">{rule.routingExplanation}</p>
						</div>
					</Card.Content>
				</Card.Root>
			{/each}
		</div>
		{/if}

		{#if editingTopic}
			<Card.Root id="team-routing-editor" class="gap-0 py-0">
				<Card.Content class="space-y-5 p-6">
					<div><h2 class="text-lg font-semibold">Configure {topicMeta(editingTopic).label}</h2><p class="text-sm text-muted-foreground">Scope: {editingScope}. Named users receive this topic because of the explanation saved beside their assignment.</p></div>

					{#if recipientsQuery.isLoading}
						<LoadingState label="Loading named recipients" variant="spinner" testid="team-routing-recipients-loading" />
					{:else if recipientsQuery.isError}
						<div class="flex flex-wrap items-center gap-3 rounded-lg border border-destructive/40 bg-destructive/5 p-4" role="alert">
							<p class="text-sm text-destructive">We couldn't load the named recipients.</p>
							<Button variant="outline" size="sm" onclick={() => recipientsQuery.refetch()}>Retry recipients</Button>
						</div>
					{:else}
						<div class="space-y-3">
							{#each Object.values(selectedRecipients) as recipient (recipient.userId)}
								<div class="grid gap-3 rounded-lg border border-border p-4 sm:grid-cols-[minmax(10rem,14rem)_1fr_auto] sm:items-center">
									<div><p class="font-medium">{recipient.displayName}</p><p class="text-xs text-muted-foreground">{recipient.email || 'No email destination'}</p></div>
									<Input aria-label={`Why ${recipient.displayName} receives this topic`} bind:value={recipient.reason} />
									<Button variant="ghost" size="icon" aria-label={`Remove ${recipient.displayName}`} onclick={() => removeRecipient(recipient.userId)}><X class="size-4" /></Button>
								</div>
							{/each}
						</div>
					{/if}

					<div class="rounded-lg border border-border p-4">
						<label for="team-routing-search" class="text-sm font-medium">Add a team member</label>
						<div class="relative mt-2"><Search class="pointer-events-none absolute left-3 top-3 size-4 text-muted-foreground" /><Input id="team-routing-search" class="pl-9" bind:value={memberSearch} placeholder="Type at least 2 characters" /></div>
						{#if memberSearch.trim().length >= 2}
							<div class="mt-2 space-y-1">
								{#if memberSearchQuery.isLoading}
									<LoadingState label="Searching team members" variant="spinner" testid="team-routing-member-search-loading" />
								{:else if memberSearchQuery.isError}
									<div class="flex flex-wrap items-center gap-2" role="alert">
										<p class="text-sm text-destructive">We couldn't search the team.</p>
										<Button variant="outline" size="sm" onclick={() => memberSearchQuery.refetch()}>Retry search</Button>
									</div>
								{:else}
									{#each memberSearchQuery.data?.items ?? [] as member (member.userId)}
										<button type="button" class="flex min-h-11 w-full items-center justify-between rounded-md px-3 py-2 text-left hover:bg-muted disabled:opacity-50" disabled={member.accessStatus !== 'Active' || member.membershipStatus !== 'Active' || !!selectedRecipients[member.userId]} onclick={() => addRecipient(member)}><span><span class="block text-sm font-medium">{member.displayName}</span><span class="block text-xs text-muted-foreground">{member.email} · {member.roleSummary || member.membershipStatus}</span></span><span class="text-xs text-muted-foreground">Add</span></button>
									{/each}
								{/if}
							</div>
						{/if}
					</div>

					<label class="flex min-h-16 items-start gap-3 rounded-lg border border-border p-4"><Checkbox checked={useFallback} onCheckedChange={(value) => (useFallback = value === true)} /><span><span class="block font-medium">Fall back to Workspace Administrators</span><span class="block text-sm text-muted-foreground">Used only when this saved rule has no named recipient. The fallback is always shown in the preview.</span></span></label>

					{#if editingRuleId && previewQuery.isLoading}
						<LoadingState label="Loading recipient preview" variant="spinner" testid="team-routing-preview-loading" />
					{:else if editingRuleId && previewQuery.isError}
						<div class="flex flex-wrap items-center gap-3 rounded-lg border border-destructive/40 bg-destructive/5 p-4">
							<p class="text-sm text-destructive">We couldn't load the current recipient preview. No routing changes have been made.</p>
							<Button variant="outline" size="sm" onclick={() => previewQuery.refetch()}>Retry preview</Button>
						</div>
					{:else if editingRuleId && previewQuery.data}
						<div class="rounded-lg bg-muted/40 p-4"><p class="text-sm font-medium">Current recipient preview</p>{#each previewQuery.data as recipient (recipient.userId)}<div class="mt-3 rounded-md border border-border/70 bg-background/60 p-3"><p class="text-sm font-medium">{recipient.displayName} · {recipient.scope}</p><p class="mt-1 text-xs text-muted-foreground">Email: {recipient.email || 'Not available'} · Phone: {recipient.phoneNumber || 'Not available'}</p><p class="mt-1 text-xs"><span class="font-medium">Enabled channels:</span> {teamRecipientChannelSummary(recipient)}</p><p class="mt-1 text-xs text-muted-foreground">{recipient.reason}</p></div>{/each}</div>
					{/if}

					<div class="flex flex-wrap items-center gap-3"><Button onclick={() => saveMutation.mutate()} disabled={saveMutation.isPending || (Object.keys(selectedRecipients).length === 0 && !useFallback)}>{saveMutation.isPending ? 'Saving…' : 'Save team routing'}</Button><Button variant="ghost" onclick={() => (editingTopic = null)}>Cancel</Button><p class="text-xs text-muted-foreground">Delivery channels come from each recipient's My alerts; tenant channels are never inherited here.</p></div>
				</Card.Content>
			</Card.Root>
		{/if}
	{/if}
</div>
