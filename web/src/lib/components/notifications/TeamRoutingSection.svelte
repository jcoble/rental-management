<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { notifications } from '$lib/api/endpoints/notifications';
	import { team, type TeamMemberSummary } from '$lib/api/endpoints/team';
	import type { TeamRoutingRecipientPreview, TeamRoutingRuleResponse, TeamRoutingTopic } from '$lib/api/types/notification';
	import { beforeNavigate } from '$app/navigation';
	import { hasCapability } from '$lib/stores/auth.svelte';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import * as Card from '$lib/components/ui/card';
	import NotificationPolicyAccordion from '$lib/components/notifications/NotificationPolicyAccordion.svelte';
	import NotificationHelpAction from '$lib/components/notifications/NotificationHelpAction.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import { Search, X } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const canManage = $derived(hasCapability('notifications.manage'));
	const topics: Array<{ topic: TeamRoutingTopic; label: string; detail: string }> = [
		{ topic: 'RentAndMoney', label: 'Rent and money', detail: 'Rent due, balances, payment problems, and money tasks.' },
		{ topic: 'ApplicationsAndLeasing', label: 'Applications and leasing', detail: 'Applications, screening, offers, and new leases.' },
		{ topic: 'WorkOrders', label: 'Repairs and maintenance', detail: 'New requests, assignments, appointments, and urgent repairs.' },
		{ topic: 'OwnerStatementsAndDecisions', label: 'Owner questions and reports', detail: 'Owner reports, approvals, and decisions that need follow-up.' },
		{ topic: 'AccountAndSecurity', label: 'Account and security', detail: 'Access changes, security alerts, and workspace administration.' },
		{ topic: 'MorningBriefing', label: 'Daily summary', detail: 'A morning list of rent, leases, appointments, inspections, and urgent work.' }
	];
	const hourOptions = Array.from({ length: 24 }, (_, hour) => ({
		value: String(hour),
		label: new Date(2026, 0, 1, hour).toLocaleTimeString([], { hour: 'numeric' })
	}));

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
			showSuccess('Daily summary schedule was saved.');
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
	let routingDirty = $state(false);

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
		if (recipient.enableInApp) channels.push('Rental Command');
		if (recipient.enableMobilePush) channels.push('phone app');
		if (recipient.enableEmail) channels.push('Email');
		if (recipient.enableSms) channels.push('text message');
		return channels.length > 0 ? channels.join(', ') : 'No alert destinations enabled';
	}

	function scopeLabel(scope: string): string {
		return scope === 'All properties' ? 'Every rental' : scope;
	}

	function responsibilitySummary(rule: TeamRoutingRuleResponse): string {
		if (rule.namedRecipientCount > 0) return rule.namedRecipientSummary;
		if (rule.useWorkspaceAdministratorFallback) {
			return 'No one assigned — administrators receive these alerts.';
		}
		return 'No one is assigned yet.';
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
		routingDirty = false;
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
		routingDirty = true;
	}

	function removeRecipient(userId: number) {
		const next = { ...selectedRecipients };
		delete next[userId];
		selectedRecipients = next;
		routingDirty = true;
	}

	function cancelEdit() {
		if (routingDirty && !window.confirm('Discard the unsaved responsibility changes?')) return;
		editingTopic = null;
		routingDirty = false;
	}

	function toggleEdit(topic: TeamRoutingTopic, rule: TeamRoutingRuleResponse) {
		if (editingTopic === topic) {
			cancelEdit();
			return;
		}
		if (routingDirty && !window.confirm('Discard the unsaved responsibility changes?')) return;
		beginEdit(topic, rule);
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
			routingDirty = false;
			showSuccess(`${topicMeta(saved.topic).label} responsibility was saved.`);
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));

	const briefingDirty = $derived.by(() => {
		const saved = briefingQuery.data;
		if (!saved) return false;
		return briefingForm.enabled !== saved.enabled ||
			briefingForm.sendHourLocal !== saved.sendHourLocal ||
			briefingForm.includeEmpty !== saved.includeEmpty;
	});
	const hasUnsavedChanges = $derived(briefingDirty || routingDirty);
	const savedSummary = $derived(
		canManage
			? 'Your saved team responsibilities and daily summary schedule are shown below.'
			: 'Your personal alert choices are available. An administrator manages team responsibilities.'
	);

	beforeNavigate(({ cancel }) => {
		if (!hasUnsavedChanges) return;
		if (!window.confirm('You have unsaved notification changes. Leave this page without saving them?')) {
			cancel();
		}
	});
</script>

<section id="team-routing" class="scroll-mt-6 space-y-4" data-testid="notifications-team-routing">
	<div class="flex flex-wrap items-start justify-between gap-4">
		<div class="max-w-3xl">
			<h2 class="text-lg font-semibold tracking-tight">Who gets told what</h2>
			<p class="mt-1 text-sm leading-6 text-muted-foreground">
				Choose who should receive alerts for rent, leasing, repairs, owner questions, and account safety.
			</p>
		</div>
		<NotificationHelpAction
			title="How team responsibilities work"
			description="Responsibilities decide who receives a kind of team alert."
			guidance="Choose the people responsible for each kind of work. Each person's saved alert choices decide whether Rental Command reaches them in the app, by email, by text message, or on their phone."
			href="/docs/daily-briefing"
			linkLabel="Open the team alerts guide"
		/>
	</div>

	<div class="rounded-lg border border-border bg-muted/30 px-4 py-3" aria-live="polite">
		<p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Saved summary</p>
		<p class="mt-1 text-sm text-foreground">{savedSummary}</p>
	</div>

	<div class="space-y-5" data-testid="team-routing-page">
		{#if !canManage}
			<Card.Root>
				<Card.Content class="p-6">
					<h2 class="font-semibold">An administrator manages team responsibilities</h2>
					<p class="mt-2 text-sm text-muted-foreground">
						Ask a workspace administrator to change who handles team work or tenant messages.
					</p>
				</Card.Content>
			</Card.Root>
		{:else if rulesQuery.isLoading}
			<LoadingState label="Loading team responsibilities" testid="team-routing-loading" />
		{:else if rulesQuery.isError}
			<Card.Root>
				<Card.Content class="space-y-4 p-6">
					<p class="text-sm text-destructive">We couldn't load team responsibilities.</p>
					<Button variant="outline" onclick={() => rulesQuery.refetch()}>Retry</Button>
				</Card.Content>
			</Card.Root>
		{:else}
			<Card.Root class="gap-0 py-0" data-testid="morning-briefing-settings">
				<Card.Content class="space-y-5 p-5">
					<div>
						<h2 class="font-semibold">Daily summary</h2>
						<p class="mt-1 text-sm text-muted-foreground">
							Send the responsible team member a morning list of what needs attention.
						</p>
					</div>
					{#if briefingQuery.isLoading}
						<LoadingState label="Loading daily summary schedule" variant="spinner" testid="morning-briefing-loading" />
					{:else if briefingQuery.isError || !briefingQuery.data}
						<div class="flex flex-wrap items-center gap-3">
							<p class="text-sm text-destructive">We couldn't load the daily summary schedule.</p>
							<Button variant="outline" onclick={() => briefingQuery.refetch()}>Retry</Button>
						</div>
					{:else}
						<div class="grid gap-4 md:grid-cols-3">
							<label class="flex min-h-20 items-start gap-3 rounded-lg border border-border p-4">
								<Checkbox
									checked={briefingForm.enabled}
									onCheckedChange={(value) => (briefingForm.enabled = value === true)}
								/>
								<span>
									<span class="block font-medium">Send every morning</span>
									<span class="block text-sm text-muted-foreground">Turn this off to pause the daily summary.</span>
								</span>
							</label>
							<div class="rounded-lg border border-border p-4">
								<label for="daily-summary-hour" class="text-sm font-medium">Send at</label>
								<Select.Root
									type="single"
									value={String(briefingForm.sendHourLocal)}
									onValueChange={(value) => value && (briefingForm.sendHourLocal = Number(value))}
								>
									<Select.Trigger id="daily-summary-hour" class="mt-2 w-full">
										{hourOptions[briefingForm.sendHourLocal]?.label ?? 'Choose a time'}
									</Select.Trigger>
									<Select.Content>
										{#each hourOptions as option (option.value)}
											<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>
										{/each}
									</Select.Content>
								</Select.Root>
								<p class="mt-2 text-xs text-muted-foreground">{briefingQuery.data.timeZone}</p>
							</div>
							<label class="flex min-h-20 items-start gap-3 rounded-lg border border-border p-4">
								<Checkbox
									checked={briefingForm.includeEmpty}
									onCheckedChange={(value) => (briefingForm.includeEmpty = value === true)}
								/>
								<span>
									<span class="block font-medium">Send even when nothing needs attention</span>
									<span class="block text-sm text-muted-foreground">The summary will say that everything is clear.</span>
								</span>
							</label>
						</div>
						<Button
							onclick={() => saveBriefingMutation.mutate()}
							disabled={saveBriefingMutation.isPending || !briefingHourIsValid}
						>
							{saveBriefingMutation.isPending ? 'Saving…' : 'Save daily summary'}
						</Button>
					{/if}
				</Card.Content>
			</Card.Root>

			{#if (rulesQuery.data?.length ?? 0) === 0}
				<Card.Root>
					<Card.Content class="space-y-3 p-6">
						<h2 class="font-semibold">Team responsibilities are unavailable</h2>
						<p class="text-sm text-muted-foreground">
							Reload this page. If the list is still missing, workspace setup needs attention.
						</p>
						<Button variant="outline" onclick={() => rulesQuery.refetch()}>Reload responsibilities</Button>
					</Card.Content>
				</Card.Root>
			{:else}
				<div class="space-y-3">
					{#each rulesQuery.data ?? [] as rule (rule.id)}
						{@const item = topicMeta(rule.topic)}
						<NotificationPolicyAccordion
							id={`team-responsibility-${rule.topic}`}
							title={item.label}
							description={item.detail}
							summary={`${scopeLabel(rule.scope)} · ${responsibilitySummary(rule)}`}
							open={editingTopic === item.topic}
							ontoggle={() => toggleEdit(item.topic, rule)}
						>
							<div id="team-routing-editor" class="space-y-5">
								<p class="text-sm text-muted-foreground">
									Choose who handles {item.label.toLowerCase()} for {scopeLabel(editingScope).toLowerCase()}.
								</p>

								{#if recipientsQuery.isLoading}
									<LoadingState label="Loading assigned people" variant="spinner" testid="team-routing-recipients-loading" />
								{:else if recipientsQuery.isError}
									<div class="flex flex-wrap items-center gap-3 rounded-lg border border-destructive/40 bg-destructive/5 p-4" role="alert">
										<p class="text-sm text-destructive">We couldn't load the assigned people.</p>
										<Button variant="outline" size="sm" onclick={() => recipientsQuery.refetch()}>Retry</Button>
									</div>
								{:else}
									<div class="space-y-3">
										{#each Object.values(selectedRecipients) as recipient (recipient.userId)}
											<div class="grid gap-3 rounded-lg border border-border p-4 sm:grid-cols-[minmax(10rem,14rem)_1fr_auto] sm:items-center">
												<div>
													<p class="font-medium">{recipient.displayName}</p>
													<p class="text-xs text-muted-foreground">{recipient.email || 'No email address'}</p>
												</div>
												<Input
													aria-label={`Why ${recipient.displayName} receives these alerts`}
													bind:value={recipient.reason}
													oninput={() => (routingDirty = true)}
												/>
												<Button variant="ghost" size="icon" aria-label={`Remove ${recipient.displayName}`} onclick={() => removeRecipient(recipient.userId)}>
													<X class="size-4" />
												</Button>
											</div>
										{/each}
									</div>
								{/if}

								<div class="rounded-lg border border-border p-4">
									<label for="team-routing-search" class="text-sm font-medium">Add a team member</label>
									<div class="relative mt-2">
										<Search class="pointer-events-none absolute left-3 top-3 size-4 text-muted-foreground" />
										<Input id="team-routing-search" class="pl-9" bind:value={memberSearch} placeholder="Search by name or email" />
									</div>
									{#if memberSearch.trim().length >= 2}
										<div class="mt-2 space-y-1">
											{#if memberSearchQuery.isLoading}
												<LoadingState label="Searching team members" variant="spinner" testid="team-routing-member-search-loading" />
											{:else if memberSearchQuery.isError}
												<div class="flex flex-wrap items-center gap-2" role="alert">
													<p class="text-sm text-destructive">We couldn't search the team.</p>
													<Button variant="outline" size="sm" onclick={() => memberSearchQuery.refetch()}>Retry</Button>
												</div>
											{:else}
												{#each memberSearchQuery.data?.items ?? [] as member (member.userId)}
													<button
														type="button"
														class="flex min-h-11 w-full items-center justify-between rounded-md px-3 py-2 text-left hover:bg-muted disabled:opacity-50"
														disabled={member.accessStatus !== 'Active' || member.membershipStatus !== 'Active' || !!selectedRecipients[member.userId]}
														onclick={() => addRecipient(member)}
													>
														<span>
															<span class="block text-sm font-medium">{member.displayName}</span>
															<span class="block text-xs text-muted-foreground">{member.email} · {member.roleSummary || member.membershipStatus}</span>
														</span>
														<span class="text-xs text-muted-foreground">Add</span>
													</button>
												{/each}
											{/if}
										</div>
									{/if}
								</div>

								<label class="flex min-h-16 items-start gap-3 rounded-lg border border-border p-4">
									<Checkbox
										checked={useFallback}
										onCheckedChange={(value) => {
											useFallback = value === true;
											routingDirty = true;
										}}
									/>
									<span>
										<span class="block font-medium">Let administrators handle unassigned alerts</span>
										<span class="block text-sm text-muted-foreground">
											When no one is assigned, active workspace administrators receive these alerts.
										</span>
									</span>
								</label>

								{#if editingRuleId && previewQuery.isLoading}
									<LoadingState label="Checking who receives these alerts" variant="spinner" testid="team-routing-preview-loading" />
								{:else if editingRuleId && previewQuery.isError}
									<div class="flex flex-wrap items-center gap-3 rounded-lg border border-destructive/40 bg-destructive/5 p-4">
										<p class="text-sm text-destructive">We couldn't check the current recipients. No changes were made.</p>
										<Button variant="outline" size="sm" onclick={() => previewQuery.refetch()}>Retry</Button>
									</div>
								{:else if editingRuleId && previewQuery.data}
									<details class="rounded-lg bg-muted/40 p-4">
										<summary class="cursor-pointer text-sm font-medium">Who receives these alerts now</summary>
										{#each previewQuery.data as recipient (recipient.userId)}
											<div class="mt-3 rounded-md border border-border/70 bg-background/60 p-3">
												<p class="text-sm font-medium">{recipient.displayName} · {scopeLabel(recipient.scope)}</p>
												<p class="mt-1 text-xs text-muted-foreground">
													Email: {recipient.email || 'Not available'} · Mobile: {recipient.phoneNumber || 'Not available'}
												</p>
												<p class="mt-1 text-xs"><span class="font-medium">Personal alert choices:</span> {teamRecipientChannelSummary(recipient)}</p>
											</div>
										{/each}
									</details>
								{/if}

								<div class="flex flex-wrap items-center gap-3">
									<Button
										onclick={() => saveMutation.mutate()}
										disabled={saveMutation.isPending || (Object.keys(selectedRecipients).length === 0 && !useFallback)}
									>
										{saveMutation.isPending ? 'Saving…' : 'Save responsibility'}
									</Button>
									<Button variant="ghost" onclick={cancelEdit}>Cancel</Button>
									<p class="text-xs text-muted-foreground">
										Each person keeps control of their own alert destinations under My alerts.
									</p>
								</div>
							</div>
						</NotificationPolicyAccordion>
					{/each}
				</div>
			{/if}
		{/if}
	</div>
</section>
