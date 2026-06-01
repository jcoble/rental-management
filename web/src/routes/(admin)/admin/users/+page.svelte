<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { adminUsers } from '$lib/api/endpoints/adminUsers';
	import type { TeamMember, UserRole, CreateTeamMemberResponse } from '$lib/types';
	import { getCurrentUser } from '$lib/stores/auth.svelte';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { Plus } from '@lucide/svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';

	const ROLES: UserRole[] = ['Admin', 'Manager', 'Agent', 'Owner', 'Tenant'];

	const queryClient = useQueryClient();
	const currentUser = $derived(getCurrentUser());

	// ---- Query ----

	const membersQuery = createQuery(() => ({
		queryKey: ['admin-users'],
		queryFn: () => adminUsers.list()
	}));

	const members = $derived((membersQuery.data ?? []) as TeamMember[]);

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['admin-users'] });
	}

	// ---- Role mutation ----

	const setRoleMutation = createMutation(() => ({
		mutationFn: ({ id, role }: { id: number; role: UserRole }) => adminUsers.setRole(id, role),
		onSuccess: () => {
			showSuccess('Role updated.');
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	// ---- Active toggle mutation ----

	const setActiveMutation = createMutation(() => ({
		mutationFn: ({ id, isActive }: { id: number; isActive: boolean }) =>
			adminUsers.setActive(id, isActive),
		onSuccess: (_r, vars) => {
			showSuccess(vars.isActive ? 'Member re-activated.' : 'Member deactivated.');
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	// ---- Invite / add member dialog ----

	const emptyForm = { email: '', displayName: '', role: 'Manager' as UserRole, temporaryPassword: '' };
	let showInviteDialog = $state(false);
	let inviteForm = $state({ ...emptyForm });

	const createMemberMutation = createMutation(() => ({
		mutationFn: () =>
			adminUsers.create({
				email: inviteForm.email,
				displayName: inviteForm.displayName || undefined,
				role: inviteForm.role,
				temporaryPassword: inviteForm.temporaryPassword || undefined
			}),
		onSuccess: (result: CreateTeamMemberResponse) => {
			showSuccess(`${result.email} added to the team.`);
			inviteForm = { ...emptyForm };
			showInviteDialog = false;
			invalidate();
			if (result.generatedPassword) {
				// Small delay so the invite dialog closes before the password dialog opens.
				setTimeout(() => {
					generatedPasswordInfo = { email: result.email, password: result.generatedPassword! };
					showPasswordDialog = true;
				}, 100);
			}
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function submitInvite() {
		if (!inviteForm.email) return;
		createMemberMutation.mutate();
	}

	// ---- One-time generated password dialog ----

	let showPasswordDialog = $state(false);
	let generatedPasswordInfo = $state<{ email: string; password: string } | null>(null);

	// ---- Helpers ----

	/** True when a control on this row would self-lock: this is the current user's own row. */
	function isSelf(member: TeamMember): boolean {
		return !!currentUser && currentUser.id === member.id;
	}

	const columns: ColumnDef<TeamMember>[] = [
		{
			key: 'member',
			title: 'Member',
			mobileRole: 'title',
			cell: memberCellSnippet,
			accessor: (m) => m.displayName || m.email,
		},
		{
			key: 'role',
			title: 'Role',
			mobileRole: 'badge',
			cell: roleCellSnippet,
			accessor: (m) => m.role,
		},
		{
			key: 'status',
			title: 'Status',
			mobileRole: 'meta',
			cell: statusCellSnippet,
			accessor: (m) => (m.isActive ? 'Active' : 'Inactive'),
		},
		{
			key: 'createdAt',
			title: 'Joined',
			format: 'date',
			sortable: true,
			mobileRole: 'meta',
		},
	];
</script>

{#snippet memberCellSnippet(member: TeamMember)}
	<div>
		<p class="font-medium" data-testid="member-name">
			{member.displayName || '—'}
			{#if isSelf(member)}
				<span class="ml-1 text-xs text-muted-foreground">(you)</span>
			{/if}
		</p>
		<p class="text-xs text-muted-foreground" data-testid="member-email">
			{member.email}
		</p>
	</div>
{/snippet}

{#snippet roleCellSnippet(member: TeamMember)}
	<Select.Root
		type="single"
		value={member.role}
		disabled={isSelf(member) || setRoleMutation.isPending || setActiveMutation.isPending}
		onValueChange={(role) => {
			if (role && role !== member.role) {
				setRoleMutation.mutate({ id: member.id, role: role as UserRole });
			}
		}}
	>
		<Select.Trigger
			class="h-8 w-36 text-xs"
			data-testid="member-role-select"
			disabled={isSelf(member)}
		>
			<Select.Value />
		</Select.Trigger>
		<Select.Content>
			{#each ROLES as r}
				<Select.Item value={r} label={r}>{r}</Select.Item>
			{/each}
		</Select.Content>
	</Select.Root>
{/snippet}

{#snippet statusCellSnippet(member: TeamMember)}
	<Button
		variant={member.isActive ? 'outline' : 'secondary'}
		size="sm"
		class="h-7 text-xs"
		disabled={isSelf(member) || setActiveMutation.isPending || setRoleMutation.isPending}
		data-testid="member-active-toggle"
		onclick={() => setActiveMutation.mutate({ id: member.id, isActive: !member.isActive })}
	>
		{member.isActive ? 'Active' : 'Inactive'}
	</Button>
{/snippet}

<svelte:head>
	<title>Team - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6" data-testid="team-page">
	<div class="mb-5 flex items-start justify-between">
		<div>
			<h1 class="text-2xl font-bold">Team</h1>
			<p class="text-sm text-muted-foreground">Manage team members, roles, and access.</p>
		</div>
		<Button data-testid="invite-member-button" onclick={() => (showInviteDialog = true)}>
			<Plus class="h-4 w-4" /> Invite member
		</Button>
	</div>

	<DataGrid
		data={members}
		{columns}
		loading={membersQuery.isPending}
		emptyMessage="No team members yet."
		getRowKey={(m) => m.id}
		getRowTestId={() => 'team-member-row'}
		data-testid="team-members-list"
	/>
</div>

<!-- Invite member dialog -->
<Dialog.Root
	open={showInviteDialog}
	onOpenChange={(v) => {
		if (!v) {
			showInviteDialog = false;
			inviteForm = { ...emptyForm };
		}
	}}
>
	<Dialog.Content class="max-w-md" data-testid="invite-dialog">
		<Dialog.Header>
			<Dialog.Title>Invite team member</Dialog.Title>
			<Dialog.Description>
				Add a new person to your team. They can sign in with the email and password you provide.
			</Dialog.Description>
		</Dialog.Header>

		<div class="space-y-3">
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Email <span class="text-destructive">*</span></span>
				<Input
					data-testid="invite-email-input"
					type="email"
					bind:value={inviteForm.email}
					placeholder="team@example.com"
				/>
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Display name</span>
				<Input
					data-testid="invite-name-input"
					bind:value={inviteForm.displayName}
					placeholder="Jane Smith"
				/>
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Role</span>
				<Select.Root type="single" bind:value={inviteForm.role}>
					<Select.Trigger class="w-full" data-testid="invite-role-select">
						{inviteForm.role}
					</Select.Trigger>
					<Select.Content>
						{#each ROLES as r}
							<Select.Item value={r} label={r}>{r}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">
					Temporary password <span class="text-muted-foreground">(leave blank to auto-generate)</span>
				</span>
				<Input
					data-testid="invite-password-input"
					type="password"
					bind:value={inviteForm.temporaryPassword}
					placeholder="Optional"
					autocomplete="new-password"
				/>
			</div>
		</div>

		<Dialog.Footer>
			<Button
				variant="outline"
				data-testid="invite-cancel"
				onclick={() => {
					showInviteDialog = false;
					inviteForm = { ...emptyForm };
				}}
			>
				Cancel
			</Button>
			<Button
				data-testid="invite-submit"
				disabled={!inviteForm.email || createMemberMutation.isPending}
				onclick={submitInvite}
			>
				{createMemberMutation.isPending ? 'Adding…' : 'Add member'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<!-- One-time generated password dialog -->
<Dialog.Root
	open={showPasswordDialog}
	onOpenChange={(v) => {
		if (!v) {
			showPasswordDialog = false;
			generatedPasswordInfo = null;
		}
	}}
>
	<Dialog.Content class="max-w-md" data-testid="generated-password-dialog">
		<Dialog.Header>
			<Dialog.Title>Temporary password — save it now</Dialog.Title>
			<Dialog.Description>
				This password will <strong>not be shown again</strong>. Copy it and share it with the new
				member securely.
			</Dialog.Description>
		</Dialog.Header>

		{#if generatedPasswordInfo}
			<div class="rounded-md border border-border bg-muted/40 p-4 space-y-2 text-sm">
				<div>
					<span class="text-xs text-muted-foreground">Email</span>
					<p class="font-medium" data-testid="generated-email">{generatedPasswordInfo.email}</p>
				</div>
				<div>
					<span class="text-xs text-muted-foreground">Temporary password</span>
					<p
						class="mt-0.5 rounded bg-background px-2 py-1.5 font-mono text-base tracking-wide border border-border"
						data-testid="generated-password"
					>
						{generatedPasswordInfo.password}
					</p>
				</div>
			</div>
		{/if}

		<Dialog.Footer>
			<Button data-testid="generated-password-close" onclick={() => {
				showPasswordDialog = false;
				generatedPasswordInfo = null;
			}}>
				I've copied the password
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
