<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { adminUsers } from '$lib/api/endpoints/adminUsers';
	import type { TeamMember, UserRole, CreateTeamMemberResponse } from '$lib/types';
	import { getAuthState, getCurrentUser } from '$lib/stores/auth.svelte';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import Pagination from '$lib/components/shared/Pagination.svelte';
	import { Loader2, Plus, Copy, Check, TriangleAlert } from '@lucide/svelte';

	const ROLES: UserRole[] = ['Admin', 'Manager', 'Agent', 'Owner', 'Tenant'];
	const PAGE_SIZE = 20;

	const queryClient = useQueryClient();
	const currentUser = $derived(getCurrentUser());
	const authState = getAuthState();
	let skip = $state(0);

	// ---- Query ----

	const membersQuery = createQuery(() => ({
		queryKey: ['admin-users', skip],
		enabled: authState.isAuthenticated,
		queryFn: () => adminUsers.listPage({ skip, take: PAGE_SIZE, sort: '-createdAt' })
	}));

	const members = $derived((membersQuery.data?.items ?? []) as TeamMember[]);
	const hasNext = $derived(skip + members.length < (membersQuery.data?.totalCount ?? 0));

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
			showSuccess(`${result.member.email} added to the team.`);
			inviteForm = { ...emptyForm };
			showInviteDialog = false;
			skip = 0;
			invalidate();
			if (result.generatedPassword) {
				// Small delay so the invite dialog closes before the password dialog opens.
				setTimeout(() => {
					generatedPasswordInfo = { email: result.member.email, password: result.generatedPassword! };
					passwordCopied = false;
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
	/** Gates the dismiss button: the modal can't be closed until the password is copied. */
	let passwordCopied = $state(false);

	/** Copy the one-time password to the clipboard and unlock dismissal. */
	async function copyGeneratedPassword() {
		if (!generatedPasswordInfo) return;
		try {
			await navigator.clipboard.writeText(generatedPasswordInfo.password);
			passwordCopied = true;
			showSuccess('Password copied to clipboard.');
		} catch {
			showError('Could not copy automatically — select the password and copy it manually.');
		}
	}

	function closePasswordDialog() {
		showPasswordDialog = false;
		generatedPasswordInfo = null;
		passwordCopied = false;
	}

	// ---- Helpers ----

	/** True when a control on this row would self-lock: this is the current user's own row. */
	function isSelf(member: TeamMember): boolean {
		if (!currentUser) return false;
		return (
			currentUser.id === member.id ||
			currentUser.email?.toLowerCase() === member.email.toLowerCase()
		);
	}

	function formatDate(date: string): string {
		const parsed = new Date(date);
		return Number.isNaN(parsed.getTime()) ? '—' : parsed.toLocaleDateString(undefined, { timeZone: 'UTC' });
	}
</script>

{#snippet memberName(member: TeamMember)}
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

{#snippet roleSelect(member: TeamMember)}
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
			{member.role}
		</Select.Trigger>
		<Select.Content>
			{#each ROLES as r}
				<Select.Item value={r} label={r}>{r}</Select.Item>
			{/each}
		</Select.Content>
	</Select.Root>
{/snippet}

{#snippet statusButton(member: TeamMember)}
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

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="team-page">
	<div class="mb-5 flex items-start justify-between">
		<div>
			<h1 class="text-2xl font-bold">Team</h1>
			<p class="text-sm text-muted-foreground">Manage team members, roles, and access.</p>
		</div>
		<Button data-testid="invite-member-button" onclick={() => (showInviteDialog = true)}>
			<Plus class="h-4 w-4" /> Invite member
		</Button>
	</div>

	<div class="rounded-lg border border-border" data-testid="team-members-list">
		{#if membersQuery.isPending}
			<div class="flex items-center justify-center gap-2 py-12 text-sm text-muted-foreground">
				<Loader2 class="h-4 w-4 animate-spin" />
				Loading team members…
			</div>
		{:else if members.length === 0}
			<div class="py-12 text-center text-sm text-muted-foreground">No team members yet.</div>
		{:else}
			<div class="hidden overflow-x-auto sm:block">
			<table class="w-full min-w-[760px] table-fixed text-sm">
				<thead class="border-b border-border text-left text-xs uppercase tracking-wide text-muted-foreground">
					<tr>
						<th class="w-[42%] px-4 py-3">Member</th>
						<th class="w-[18%] px-4 py-3">Role</th>
						<th class="w-[18%] px-4 py-3">Status</th>
						<th class="w-[22%] px-4 py-3 text-right">Joined</th>
					</tr>
				</thead>
				<tbody>
					{#each members as member (member.id)}
						<tr class="border-b border-border/60 last:border-0" data-testid="team-member-row">
							<td class="px-4 py-3">{@render memberName(member)}</td>
							<td class="px-4 py-3">{@render roleSelect(member)}</td>
							<td class="px-4 py-3">{@render statusButton(member)}</td>
							<td class="px-4 py-3 text-right text-muted-foreground">{formatDate(member.createdAt)}</td>
						</tr>
					{/each}
				</tbody>
			</table>
			</div>

			<div class="divide-y divide-border sm:hidden">
				{#each members as member (member.id)}
					<div class="space-y-3 p-4" data-testid="team-member-row">
						{@render memberName(member)}
						<div class="grid grid-cols-2 gap-3">
							<div>
								<p class="mb-1 text-xs text-muted-foreground">Role</p>
								{@render roleSelect(member)}
							</div>
							<div>
								<p class="mb-1 text-xs text-muted-foreground">Status</p>
								{@render statusButton(member)}
							</div>
						</div>
						<p class="text-xs text-muted-foreground">Joined {formatDate(member.createdAt)}</p>
					</div>
				{/each}
			</div>
		{/if}
		{#if !membersQuery.isPending && members.length > 0}
			<div class="border-t border-border px-4 py-3">
				<Pagination
					bind:skip
					take={PAGE_SIZE}
					count={members.length}
					{hasNext}
					testid="team-pagination"
				/>
			</div>
		{/if}
	</div>
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
		// Refuse to close until the password has been copied — otherwise the
		// credential is lost for good (it's never shown again).
		if (!v && passwordCopied) {
			closePasswordDialog();
		}
	}}
>
	<Dialog.Content
		class="max-w-md"
		data-testid="generated-password-dialog"
		showCloseButton={passwordCopied}
		onEscapeKeydown={(e) => { if (!passwordCopied) e.preventDefault(); }}
		onInteractOutside={(e) => { if (!passwordCopied) e.preventDefault(); }}
	>
		<Dialog.Header>
			<Dialog.Title>Temporary password — save it now</Dialog.Title>
			<Dialog.Description>
				This is the <strong>only</strong> time this password is ever shown. Copy it and share it
				with the new member securely.
			</Dialog.Description>
		</Dialog.Header>

		<!-- Unmissable last-chance warning: this credential cannot be recovered. -->
		<div
			class="flex items-start gap-3 rounded-md border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive"
			data-testid="generated-password-warning"
			role="alert"
		>
			<TriangleAlert class="mt-0.5 h-5 w-5 shrink-0" />
			<p>
				<strong>This is the only time this password will ever be shown.</strong> If you lose it,
				the new member can't sign in and you'll have to reset it. Copy it now and share it securely.
			</p>
		</div>

		{#if generatedPasswordInfo}
			<div class="rounded-md border border-border bg-muted/40 p-4 space-y-3 text-sm">
				<div>
					<span class="text-xs text-muted-foreground">Email</span>
					<p class="font-medium" data-testid="generated-email">{generatedPasswordInfo.email}</p>
				</div>
				<div>
					<span class="text-xs text-muted-foreground">Temporary password</span>
					<div class="mt-0.5 flex items-center gap-2">
						<p
							class="flex-1 rounded bg-background px-2 py-1.5 font-mono text-base tracking-wide border border-border break-all"
							data-testid="generated-password"
						>
							{generatedPasswordInfo.password}
						</p>
						<Button
							variant="outline"
							size="sm"
							class="shrink-0"
							data-testid="generated-password-copy"
							onclick={copyGeneratedPassword}
						>
							{#if passwordCopied}
								<Check class="h-4 w-4" />
								Copied
							{:else}
								<Copy class="h-4 w-4" />
								Copy
							{/if}
						</Button>
					</div>
				</div>
			</div>
		{/if}

		<Dialog.Footer>
			<Button
				variant="outline"
				data-testid="generated-password-manual-confirm"
				onclick={() => {
					passwordCopied = true;
					showSuccess('Password marked as saved.');
				}}
			>
				I saved it manually
			</Button>
			<Button
				data-testid="generated-password-close"
				disabled={!passwordCopied}
				onclick={closePasswordDialog}
			>
				I've copied the password
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
