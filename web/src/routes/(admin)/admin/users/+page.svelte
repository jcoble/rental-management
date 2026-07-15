<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import {
		team,
		type AssignmentScopeKind,
		type TeamAssignmentSummary,
		type TeamMemberSummary,
		type WorkspaceTeamMutationResult,
		type AtomicTeamResponse
	} from '$lib/api/endpoints/team';
	import { properties } from '$lib/api/endpoints/properties';
	import { getAuthState, getCurrentUser, hasCapability } from '$lib/stores/auth.svelte';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import Pagination from '$lib/components/shared/Pagination.svelte';
	import { Loader2, Plus, Search, UserRoundCheck } from '@lucide/svelte';

	const PAGE_SIZE = 20;
	const PROPERTY_PAGE_SIZE = 20;
	const queryClient = useQueryClient();
	const authState = getAuthState();
	const currentUser = $derived(getCurrentUser());
	const canManageTeam = $derived(hasCapability('team.manage'));
	const portfolioId = $derived(authState.accessEnvelope?.selectedContext.portfolioId ?? 0);
	let skip = $state(0);
	let search = $state('');
	let propertySkip = $state(0);
	let propertySearch = $state('');

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
		queryKey: ['team-scope-properties', portfolioId, propertySkip, propertySearch.trim()],
		enabled: authState.isAuthenticated && canManageTeam && portfolioId > 0,
		queryFn: () => properties.listPage(portfolioId, {
			skip: propertySkip,
			take: PROPERTY_PAGE_SIZE,
			search: propertySearch,
			sort: 'name'
		})
	}));

	const members = $derived(membersQuery.data?.items ?? []);
	const hasNext = $derived(skip + members.length < (membersQuery.data?.totalCount ?? 0));
	const propertyChoices = $derived(propertiesQuery.data?.items ?? []);
	const hasNextPropertyPage = $derived(
		propertySkip + propertyChoices.length < (propertiesQuery.data?.totalCount ?? 0)
	);

	function resetPropertyPicker() {
		propertySkip = 0;
		propertySearch = '';
	}

	function invalidateMembers() {
		void queryClient.invalidateQueries({ queryKey: ['team-members'] });
	}

	function isSelf(member: TeamMemberSummary) {
		return currentUser?.id === member.userId;
	}

	function formatDate(value: string) {
		return new Date(value).toLocaleDateString(undefined, { timeZone: 'UTC' });
	}

	function scopeLabel(scope: AssignmentScopeKind, propertyCount?: number) {
		if (scope === 'AllProperties') return 'All properties';
		if (scope === 'AssignedWorkOrders') return 'Only assigned work orders';
		if (propertyCount === undefined) return 'Selected properties';
		return `${propertyCount} selected ${propertyCount === 1 ? 'property' : 'properties'}`;
	}

	function scopeChoicesFor(roleProfileKey: string): AssignmentScopeKind[] {
		if (roleProfileKey === 'workspace-administrator') return ['AllProperties'];
		if (roleProfileKey === 'maintenance-technician') return ['AssignedWorkOrders'];
		return ['SelectedProperties', 'AllProperties'];
	}

	function scopeDescription(scope: AssignmentScopeKind) {
		if (scope === 'AssignedWorkOrders') {
			return 'Assigned-work scope does not grant broad property access; this person sees only work orders specifically assigned to them.';
		}
		return scope === 'AllProperties'
			? 'Property scope covers every rental in the workspace.'
			: 'Property scope covers only the rentals selected below.';
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
	const scopeChoices = $derived(scopeChoicesFor(roleProfileKey));
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
		resetPropertyPicker();
	}

	function toggleProperty(ids: number[], id: number, checked: boolean) {
		return checked ? [...new Set([...ids, id])] : ids.filter((propertyId) => propertyId !== id);
	}

	function resetInvite() {
		email = '';
		displayName = '';
		roleProfileKey = '';
		scopeKind = 'SelectedProperties';
		selectedPropertyIds = [];
		resetPropertyPicker();
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

	let showAssignments = $state(false);
	let selectedMember = $state<TeamMemberSummary | null>(null);
	let assignmentSkip = $state(0);
	const assignmentsQuery = createQuery(() => ({
		queryKey: ['team-assignments', selectedMember?.accessContextId, assignmentSkip],
		enabled: showAssignments && selectedMember !== null,
		queryFn: () => team.assignments(selectedMember!.accessContextId, { skip: assignmentSkip, take: 50 })
	}));
	const hasNextAssignmentPage = $derived(
		assignmentSkip + (assignmentsQuery.data?.items.length ?? 0) <
			(assignmentsQuery.data?.totalCount ?? 0)
	);

	function openAssignments(member: TeamMemberSummary) {
		selectedMember = member;
		assignmentSkip = 0;
		showAssignments = true;
	}

	function applyMutationRevision(result: AtomicTeamResponse<WorkspaceTeamMutationResult>) {
		if (selectedMember) {
			selectedMember = { ...selectedMember, accessRevision: result.value.accessRevision };
		}
		invalidateMembers();
		void queryClient.invalidateQueries({ queryKey: ['team-assignments'] });
	}

	let showAssignmentEditor = $state(false);
	let editingAssignment = $state<TeamAssignmentSummary | null>(null);
	let assignmentRoleProfileKey = $state('');
	let assignmentScopeKind = $state<AssignmentScopeKind>('SelectedProperties');
	let assignmentPropertyIds = $state<number[]>([]);
	const assignmentRole = $derived(
		roleProfilesQuery.data?.find((role) => role.key === assignmentRoleProfileKey) ?? null
	);
	const assignmentScopeChoices = $derived(scopeChoicesFor(assignmentRoleProfileKey));
	const assignmentEditorDisabled = $derived(
		!selectedMember ||
		(!editingAssignment && !assignmentRoleProfileKey) ||
		(assignmentScopeKind === 'SelectedProperties' && assignmentPropertyIds.length === 0)
	);

	function resetAssignmentEditor() {
		editingAssignment = null;
		assignmentRoleProfileKey = '';
		assignmentScopeKind = 'SelectedProperties';
		assignmentPropertyIds = [];
		resetPropertyPicker();
	}

	function beginAddAssignment() {
		resetAssignmentEditor();
		showAssignmentEditor = true;
	}

	function beginReplaceProperties(assignment: TeamAssignmentSummary) {
		editingAssignment = assignment;
		assignmentRoleProfileKey = assignment.roleProfileKey;
		assignmentScopeKind = 'SelectedProperties';
		assignmentPropertyIds = [];
		resetPropertyPicker();
		showAssignmentEditor = true;
	}

	function chooseAssignmentRole(key: string) {
		assignmentRoleProfileKey = key;
		const role = roleProfilesQuery.data?.find((item) => item.key === key);
		assignmentScopeKind = role?.defaultScopeKind ?? 'SelectedProperties';
		assignmentPropertyIds = [];
		resetPropertyPicker();
	}

	const assignmentMutation = createMutation(() => ({
		mutationFn: () => {
			if (!selectedMember) throw new Error('Choose a team member.');
			const propertyIds = assignmentPropertyIds.toSorted((a, b) => a - b);
			if (editingAssignment) {
				return team.replaceAssignmentProperties(
					selectedMember.accessContextId,
					editingAssignment.assignmentId,
					selectedMember.accessRevision,
					propertyIds
				);
			}
			return team.addAssignment(selectedMember.accessContextId, {
				expectedAccessRevision: selectedMember.accessRevision,
				roleProfileKey: assignmentRoleProfileKey,
				scopeKind: assignmentScopeKind,
				selectedPropertyIds: assignmentScopeKind === 'SelectedProperties' ? propertyIds : [],
				effectiveFromUtc: new Date().toISOString()
			});
		},
		onSuccess: (result) => {
			showSuccess(editingAssignment ? 'Property scope replaced.' : 'Assignment added.');
			applyMutationRevision(result);
			showAssignmentEditor = false;
			resetAssignmentEditor();
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));

	const endAssignmentMutation = createMutation(() => ({
		mutationFn: (assignment: TeamAssignmentSummary) => {
			if (!selectedMember) throw new Error('Choose a team member.');
			return team.endAssignment(
				selectedMember.accessContextId,
				assignment.assignmentId,
				selectedMember.accessRevision,
				new Date().toISOString()
			);
		},
		onSuccess: (result) => {
			showSuccess('Assignment ended.');
			applyMutationRevision(result);
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));
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
		{#if canManageTeam}
			<Button data-testid="invite-member-button" onclick={() => (showInvite = true)}>
				<Plus class="h-4 w-4" /> Add team member
			</Button>
		{/if}
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
						<div class="flex items-center gap-2">
							<Button variant="outline" size="sm" data-testid="view-assignments-button" onclick={() => openAssignments(member)}>
								Assignments
							</Button>
							{#if canManageTeam}
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
							{:else}
								<span class="rounded-full bg-muted px-2.5 py-1 text-xs font-medium">{member.membershipStatus}</span>
							{/if}
						</div>
					</div>
				{/each}
			</div>
			<div class="border-t border-border px-4 py-3">
				<Pagination bind:skip take={PAGE_SIZE} count={members.length} {hasNext} testid="team-pagination" />
			</div>
		{/if}
	</div>
</div>

{#if canManageTeam}
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
					<select class="m3-field-surface h-10 w-full px-3" value={roleProfileKey} onchange={(event) => chooseRole(event.currentTarget.value)}>
						<option value="">Choose a job…</option>
						{#each roleProfilesQuery.data ?? [] as role}<option value={role.key}>{role.displayName}</option>{/each}
					</select>
				</label>
				{#if selectedRole}<p class="text-sm text-muted-foreground">{selectedRole.description}</p>{/if}

				{#if roleProfileKey}
					<label class="block space-y-1 text-sm">
						Access scope
						<select class="m3-field-surface h-10 w-full px-3" bind:value={scopeKind}>
							{#each scopeChoices as scope}<option value={scope}>{scopeLabel(scope)}</option>{/each}
						</select>
					</label>
					<p class="text-xs text-muted-foreground">{scopeDescription(scopeKind)}</p>
				{/if}

				{#if scopeKind === 'SelectedProperties' && roleProfileKey}
					<fieldset class="rounded-[var(--m3-shape-medium)] border border-border p-3">
						<legend class="px-1 text-sm font-medium">Properties</legend>
						<label class="relative mt-1 block">
							<span class="sr-only">Search properties</span>
							<Search class="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
							<Input class="pl-9" placeholder="Search properties" value={propertySearch} oninput={(event) => { propertySearch = (event.currentTarget as HTMLInputElement).value; propertySkip = 0; }} />
						</label>
						<p class="mt-2 text-xs text-muted-foreground">{selectedPropertyIds.length} selected</p>
						<div class="mt-1 grid max-h-44 gap-2 overflow-y-auto sm:grid-cols-2">
							{#each propertyChoices as property}
								<label class="flex items-center gap-2 text-sm">
									<input type="checkbox" checked={selectedPropertyIds.includes(property.id)} onchange={(event) => (selectedPropertyIds = toggleProperty(selectedPropertyIds, property.id, event.currentTarget.checked))} />
									<span class="truncate">{property.name}</span>
								</label>
							{:else}
								<p class="text-sm text-muted-foreground">No matching properties.</p>
							{/each}
						</div>
						<Pagination bind:skip={propertySkip} take={PROPERTY_PAGE_SIZE} count={propertyChoices.length} hasNext={hasNextPropertyPage} testid="invite-property-pagination" />
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
{/if}

<Dialog.Root
	open={showAssignments}
	onOpenChange={(open) => {
		showAssignments = open;
	}}
>
	<Dialog.Content class="max-w-2xl" data-testid="assignments-dialog">
		<Dialog.Header>
			<Dialog.Title>{selectedMember?.displayName ?? 'Team member'} assignments</Dialog.Title>
			<Dialog.Description>
				Property scope grants access across all or selected rentals. Assigned-work scope grants access only to work orders specifically assigned to the person.
			</Dialog.Description>
		</Dialog.Header>
		<div class="max-h-[65vh] space-y-3 overflow-y-auto pr-1">
			<div class="rounded-[var(--m3-shape-medium)] bg-muted/50 p-3 text-sm text-muted-foreground">
				A person can hold several separately scoped assignments. Owners and tenants remain relationship-based experiences and never appear as Team jobs.
			</div>
			{#if assignmentsQuery.isPending}
				<div class="flex items-center justify-center gap-2 py-8 text-sm text-muted-foreground"><Loader2 class="h-4 w-4 animate-spin" /> Loading assignments…</div>
			{:else if assignmentsQuery.isError}
				<p class="py-6 text-center text-sm text-destructive">{apiErrorMessage(assignmentsQuery.error)}</p>
			{:else if (assignmentsQuery.data?.items.length ?? 0) === 0}
				<p class="py-6 text-center text-sm text-muted-foreground">No assignments yet.</p>
			{:else}
				{#each assignmentsQuery.data?.items ?? [] as assignment (assignment.assignmentId)}
					<div class="rounded-[var(--m3-shape-medium)] border border-border p-4" data-testid="team-assignment-row">
						<div class="flex flex-wrap items-start justify-between gap-3">
							<div>
								<p class="font-medium">{assignment.roleProfileName}</p>
								<p class="text-sm text-muted-foreground">{scopeLabel(assignment.scopeKind, assignment.selectedPropertyCount)}</p>
								<p class="mt-1 text-xs text-muted-foreground">
									Started {formatDate(assignment.effectiveFromUtc)}{#if assignment.effectiveToUtc} · ended {formatDate(assignment.effectiveToUtc)}{/if}
								</p>
							</div>
							<span class="rounded-full bg-muted px-2.5 py-1 text-xs font-medium">{assignment.status}</span>
						</div>
						{#if canManageTeam && assignment.status === 'Active'}
							<div class="mt-3 flex flex-wrap gap-2">
								{#if assignment.roleProfileKey === 'property-manager' || assignment.roleProfileKey === 'leasing-agent'}
									<Button variant="outline" size="sm" onclick={() => beginReplaceProperties(assignment)}>Replace property scope</Button>
								{/if}
								<Button
									variant="outline"
									size="sm"
									disabled={selectedMember ? isSelf(selectedMember) || endAssignmentMutation.isPending : true}
									onclick={() => {
										if (confirm(`End the ${assignment.roleProfileName} assignment now?`)) endAssignmentMutation.mutate(assignment);
									}}
								>
									End assignment
								</Button>
							</div>
						{/if}
					</div>
				{/each}
				{#if (assignmentsQuery.data?.totalCount ?? 0) > 50}
					<Pagination bind:skip={assignmentSkip} take={50} count={assignmentsQuery.data?.items.length ?? 0} hasNext={hasNextAssignmentPage} testid="assignment-pagination" />
				{/if}
			{/if}
		</div>
		<Dialog.Footer>
			<Button variant="outline" onclick={() => (showAssignments = false)}>Close</Button>
			{#if canManageTeam && selectedMember?.membershipStatus === 'Active'}
				<Button data-testid="add-assignment-button" onclick={beginAddAssignment}><Plus class="h-4 w-4" /> Add assignment</Button>
			{/if}
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

{#if canManageTeam}
	<Dialog.Root
		open={showAssignmentEditor}
		onOpenChange={(open) => {
			showAssignmentEditor = open;
			if (!open) resetAssignmentEditor();
		}}
	>
		<Dialog.Content class="max-w-xl" data-testid="assignment-editor-dialog">
			<Dialog.Header>
				<Dialog.Title>{editingAssignment ? 'Replace property scope' : 'Add another assignment'}</Dialog.Title>
				<Dialog.Description>
					{editingAssignment
						? 'Choose the complete replacement property list. The previous selected-property scope will be removed.'
						: 'Assignments are independent, so this job can have its own property or assigned-work scope.'}
				</Dialog.Description>
			</Dialog.Header>
			<div class="max-h-[60vh] space-y-4 overflow-y-auto pr-1">
				{#if !editingAssignment}
					<label class="block space-y-1 text-sm">
						Job
						<select class="m3-field-surface h-10 w-full px-3" value={assignmentRoleProfileKey} onchange={(event) => chooseAssignmentRole(event.currentTarget.value)}>
							<option value="">Choose a job…</option>
							{#each roleProfilesQuery.data ?? [] as role}<option value={role.key}>{role.displayName}</option>{/each}
						</select>
					</label>
					{#if assignmentRole}<p class="text-sm text-muted-foreground">{assignmentRole.description}</p>{/if}
					{#if assignmentRoleProfileKey}
						<label class="block space-y-1 text-sm">
							Access scope
							<select class="m3-field-surface h-10 w-full px-3" bind:value={assignmentScopeKind}>
								{#each assignmentScopeChoices as scope}<option value={scope}>{scopeLabel(scope)}</option>{/each}
							</select>
						</label>
						<p class="text-xs text-muted-foreground">{scopeDescription(assignmentScopeKind)}</p>
					{/if}
				{/if}
				{#if assignmentScopeKind === 'SelectedProperties' && assignmentRoleProfileKey}
					<fieldset class="rounded-[var(--m3-shape-medium)] border border-border p-3">
						<legend class="px-1 text-sm font-medium">Complete property scope</legend>
						<label class="relative mt-1 block">
							<span class="sr-only">Search properties</span>
							<Search class="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
							<Input class="pl-9" placeholder="Search properties" value={propertySearch} oninput={(event) => { propertySearch = (event.currentTarget as HTMLInputElement).value; propertySkip = 0; }} />
						</label>
						<p class="mt-2 text-xs text-muted-foreground">{assignmentPropertyIds.length} selected</p>
						<div class="mt-1 grid max-h-52 gap-2 overflow-y-auto sm:grid-cols-2">
							{#each propertyChoices as property}
								<label class="flex items-center gap-2 text-sm">
									<input type="checkbox" checked={assignmentPropertyIds.includes(property.id)} onchange={(event) => (assignmentPropertyIds = toggleProperty(assignmentPropertyIds, property.id, event.currentTarget.checked))} />
									<span class="truncate">{property.name}</span>
								</label>
							{:else}
								<p class="text-sm text-muted-foreground">No matching properties.</p>
							{/each}
						</div>
						<Pagination bind:skip={propertySkip} take={PROPERTY_PAGE_SIZE} count={propertyChoices.length} hasNext={hasNextPropertyPage} testid="assignment-property-pagination" />
					</fieldset>
				{/if}
			</div>
			<Dialog.Footer>
				<Button variant="outline" onclick={() => (showAssignmentEditor = false)}>Cancel</Button>
				<Button disabled={assignmentEditorDisabled || assignmentMutation.isPending} onclick={() => assignmentMutation.mutate()}>
					{#if assignmentMutation.isPending}<Loader2 class="h-4 w-4 animate-spin" />{/if}
					{editingAssignment ? 'Replace properties' : 'Add assignment'}
				</Button>
			</Dialog.Footer>
		</Dialog.Content>
	</Dialog.Root>
{/if}
