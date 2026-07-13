<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { team, type AssignmentScopeKind, type TeamMemberSummary } from '$lib/api/endpoints/team';
	import { properties } from '$lib/api/endpoints/properties';
	import { getAuthState, getCurrentUser } from '$lib/stores/auth.svelte';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import Pagination from '$lib/components/shared/Pagination.svelte';
	import { Loader2, Plus, Search, UserRoundCheck } from '@lucide/svelte';

	const PAGE_SIZE = 20;
	const queryClient = useQueryClient();
	const authState = getAuthState();
	const currentUser = $derived(getCurrentUser());
	const portfolioId = $derived(authState.accessEnvelope?.selectedContext.portfolioId ?? 0);
	let skip = $state(0);
	let search = $state('');

	const membersQuery = createQuery(() => ({
		queryKey: ['team-members', skip, search.trim()],
		enabled: authState.isAuthenticated,
		queryFn: () => team.members({ skip, take: PAGE_SIZE, search, sort: '-createdAt' })
	}));
	const roleProfilesQuery = createQuery(() => ({
		queryKey: ['team-role-profiles'],
		enabled: authState.isAuthenticated,
		queryFn: team.roleProfiles
	}));
	const propertiesQuery = createQuery(() => ({
		queryKey: ['team-scope-properties', portfolioId],
		enabled: authState.isAuthenticated && portfolioId > 0,
		queryFn: () => properties.list(portfolioId, { take: 250, sort: 'name' })
	}));

	const members = $derived(membersQuery.data?.items ?? []);
	const hasNext = $derived(skip + members.length < (membersQuery.data?.totalCount ?? 0));

	function invalidateMembers() {
		void queryClient.invalidateQueries({ queryKey: ['team-members'] });
	}

	let showInvite = $state(false);
	let email = $state('');
	let displayName = $state('');
	let roleProfileKey = $state('');
	let scopeKind = $state<AssignmentScopeKind>('SelectedProperties');
	let selectedPropertyIds = $state<number[]>([]);

	const selectedRole = $derived(
		roleProfilesQuery.data?.find((role) => role.key === roleProfileKey) ?? null
	);
	const scopeChoices = $derived.by((): AssignmentScopeKind[] => {
		if (roleProfileKey === 'workspace-administrator') return ['AllProperties'];
		if (roleProfileKey === 'maintenance-technician') return ['AssignedWorkOrders'];
		return ['SelectedProperties', 'AllProperties'];
	});
	const inviteDisabled = $derived(
		!email.trim() ||
		!displayName.trim() ||
		!roleProfileKey ||
		(scopeKind === 'SelectedProperties' && selectedPropertyIds.length === 0)
	);

	function chooseRole(key: string) {
		roleProfileKey = key;
		const role = roleProfilesQuery.data?.find((item) => item.key === key);
		scopeKind = role?.defaultScopeKind ?? 'SelectedProperties';
		selectedPropertyIds = [];
	}

	function toggleProperty(id: number, checked: boolean) {
		selectedPropertyIds = checked
			? [...new Set([...selectedPropertyIds, id])]
			: selectedPropertyIds.filter((propertyId) => propertyId !== id);
	}

	function resetInvite() {
		email = '';
		displayName = '';
		roleProfileKey = '';
		scopeKind = 'SelectedProperties';
		selectedPropertyIds = [];
	}

	const inviteMutation = createMutation(() => ({
		mutationFn: () =>
			team.createMembership({
				email: email.trim(),
				displayName: displayName.trim(),
				roleProfileKey,
				scopeKind,
				selectedPropertyIds:
					scopeKind === 'SelectedProperties' ? selectedPropertyIds.toSorted((a, b) => a - b) : [],
				effectiveFromUtc: new Date().toISOString()
			}),
		onSuccess: (result) => {
			showSuccess(
				result.value.requiresAccountActivation
					? 'Team member added. Their activation email is queued.'
					: 'Team access added to the existing account.'
			);
			showInvite = false;
			resetInvite();
			skip = 0;
			invalidateMembers();
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));

	const statusMutation = createMutation(() => ({
		mutationFn: ({ member, action }: { member: TeamMemberSummary; action: 'Suspend' | 'Reactivate' }) =>
			team.changeStatus(member.accessContextId, member.accessRevision, action),
		onSuccess: () => {
			showSuccess('Team access updated.');
			invalidateMembers();
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));

	function isSelf(member: TeamMemberSummary) {
		return currentUser?.id === member.userId;
	}

	function formatDate(value: string) {
		return new Date(value).toLocaleDateString(undefined, { timeZone: 'UTC' });
	}
</script>

<svelte:head><title>Team - Rental Command</title></svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="team-page">
	<div class="mb-5 flex flex-wrap items-start justify-between gap-3">
		<div>
			<h1 class="text-2xl font-bold">Team</h1>
			<p class="mt-1 max-w-2xl text-sm text-muted-foreground">
				Give each person a clear job and only the properties or assigned work they need.
				Owners and tenants are managed from their relationship records, not as Team roles.
			</p>
		</div>
		<Button data-testid="invite-member-button" onclick={() => (showInvite = true)}>
			<Plus class="h-4 w-4" /> Add team member
		</Button>
	</div>

	<div class="mb-4 max-w-md">
		<label for="team-search" class="sr-only">Search team members</label>
		<div class="relative">
			<Search class="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
			<Input
				id="team-search"
				class="pl-9"
				placeholder="Search by name or email"
				value={search}
				oninput={(event) => {
					search = (event.currentTarget as HTMLInputElement).value;
					skip = 0;
				}}
			/>
		</div>
	</div>

	<div class="overflow-hidden rounded-[var(--m3-shape-large)] border border-border bg-card">
		{#if membersQuery.isPending}
			<div class="flex items-center justify-center gap-2 py-12 text-sm text-muted-foreground">
				<Loader2 class="h-4 w-4 animate-spin" /> Loading team…
			</div>
		{:else if members.length === 0}
			<div class="flex flex-col items-center gap-2 px-6 py-12 text-center">
				<UserRoundCheck class="h-8 w-8 text-muted-foreground" />
				<p class="font-medium">No matching team members</p>
				<p class="text-sm text-muted-foreground">Add the people who help run rentals, leasing, or maintenance.</p>
			</div>
		{:else}
			<div class="divide-y divide-border">
				{#each members as member (member.accessContextId)}
					<div class="flex flex-wrap items-center gap-4 p-4" data-testid="team-member-row">
						<div class="min-w-0 flex-1">
							<p class="truncate font-medium">
								{member.displayName}{#if isSelf(member)} <span class="text-xs text-muted-foreground">(you)</span>{/if}
							</p>
							<p class="truncate text-sm text-muted-foreground">{member.email}</p>
						</div>
						<div class="min-w-48">
							<p class="text-sm font-medium">{member.roleSummary || 'No active assignment'}</p>
							<p class="text-xs text-muted-foreground">
								{member.assignmentCount} {member.assignmentCount === 1 ? 'assignment' : 'assignments'} · added {formatDate(member.createdAtUtc)}
							</p>
						</div>
						<Button
							variant="outline"
							size="sm"
							disabled={isSelf(member) || statusMutation.isPending}
							onclick={() =>
								statusMutation.mutate({
									member,
									action: member.membershipStatus === 'Active' ? 'Suspend' : 'Reactivate'
								})}
						>
							{member.membershipStatus === 'Active' ? 'Active' : 'Suspended'}
						</Button>
					</div>
				{/each}
			</div>
			<div class="border-t border-border px-4 py-3">
				<Pagination bind:skip take={PAGE_SIZE} count={members.length} {hasNext} testid="team-pagination" />
			</div>
		{/if}
	</div>
</div>

<Dialog.Root
	open={showInvite}
	onOpenChange={(open) => {
		showInvite = open;
		if (!open) resetInvite();
	}}
>
	<Dialog.Content class="max-w-xl" data-testid="invite-dialog">
		<Dialog.Header>
			<Dialog.Title>Add a team member</Dialog.Title>
			<Dialog.Description>
				Choose what this person does and where they can do it. New accounts receive a secure activation email.
			</Dialog.Description>
		</Dialog.Header>

		<div class="max-h-[65vh] space-y-4 overflow-y-auto pr-1">
			<div class="grid gap-3 sm:grid-cols-2">
				<label class="space-y-1 text-sm">Name <Input bind:value={displayName} placeholder="Jane Smith" /></label>
				<label class="space-y-1 text-sm">Email <Input bind:value={email} type="email" placeholder="jane@example.com" /></label>
			</div>

			<label class="block space-y-1 text-sm">
				Job
				<select
					class="m3-field-surface h-10 w-full px-3"
					value={roleProfileKey}
					onchange={(event) => chooseRole(event.currentTarget.value)}
				>
					<option value="">Choose a job…</option>
					{#each roleProfilesQuery.data ?? [] as role}
						<option value={role.key}>{role.displayName}</option>
					{/each}
				</select>
			</label>
			{#if selectedRole}<p class="text-sm text-muted-foreground">{selectedRole.description}</p>{/if}

			{#if roleProfileKey}
				<label class="block space-y-1 text-sm">
					Access scope
					<select class="m3-field-surface h-10 w-full px-3" bind:value={scopeKind}>
						{#each scopeChoices as scope}
							<option value={scope}>
								{scope === 'AllProperties' ? 'All properties' : scope === 'SelectedProperties' ? 'Selected properties' : 'Only assigned work orders'}
							</option>
						{/each}
					</select>
				</label>
			{/if}

			{#if scopeKind === 'SelectedProperties' && roleProfileKey}
				<fieldset class="rounded-[var(--m3-shape-medium)] border border-border p-3">
					<legend class="px-1 text-sm font-medium">Properties</legend>
					<div class="mt-1 grid max-h-44 gap-2 overflow-y-auto sm:grid-cols-2">
						{#each propertiesQuery.data ?? [] as property}
							<label class="flex items-center gap-2 text-sm">
								<input
									type="checkbox"
									checked={selectedPropertyIds.includes(property.id)}
									onchange={(event) => toggleProperty(property.id, event.currentTarget.checked)}
								/>
								<span class="truncate">{property.name}</span>
							</label>
						{/each}
					</div>
				</fieldset>
			{/if}
		</div>

		<Dialog.Footer>
			<Button variant="outline" onclick={() => (showInvite = false)}>Cancel</Button>
			<Button disabled={inviteDisabled || inviteMutation.isPending} onclick={() => inviteMutation.mutate()}>
				{#if inviteMutation.isPending}<Loader2 class="h-4 w-4 animate-spin" />{/if}
				Add member
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
