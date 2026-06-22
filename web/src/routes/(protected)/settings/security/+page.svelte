<script lang="ts">
	import { enhance } from '$app/forms';
	import type { ActionData } from './$types';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Card from '$lib/components/ui/card';
	import { ArrowLeft, Check, Eye, EyeOff, Shield, X } from '@lucide/svelte';

	let { form }: { form: ActionData } = $props();

	let submitting = $state(false);

	let currentPassword = $state('');
	let newPassword = $state('');
	let confirmPassword = $state('');

	let showCurrent = $state(false);
	let showNew = $state(false);
	let showConfirm = $state(false);

	// Live password-policy hints (mirror the API's Identity policy: 8+ chars, upper, lower, digit).
	const hasLength = $derived(newPassword.length >= 8);
	const hasUpper = $derived(/[A-Z]/.test(newPassword));
	const hasLower = $derived(/[a-z]/.test(newPassword));
	const hasDigit = $derived(/[0-9]/.test(newPassword));
	const passwordsMatch = $derived(
		confirmPassword.length > 0 && newPassword === confirmPassword
	);

	const canSubmit = $derived(
		currentPassword.length > 0 &&
			hasLength &&
			hasUpper &&
			hasLower &&
			hasDigit &&
			passwordsMatch &&
			!submitting
	);

	// Clear the fields after a successful change so the form isn't left holding the secret.
	$effect(() => {
		if (form?.changed) {
			currentPassword = '';
			newPassword = '';
			confirmPassword = '';
		}
	});
</script>

<svelte:head>
	<title>Security - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20">
	<div class="mb-4 max-w-xl">
		<a
			href="/settings"
			class="mb-3 inline-flex items-center gap-1.5 text-sm font-medium text-muted-foreground hover:text-foreground"
			data-testid="security-back-to-settings"
		>
			<ArrowLeft class="h-4 w-4" /> Back to settings
		</a>
		<div class="flex items-center gap-2">
			<Shield class="h-5 w-5 text-primary" />
			<h1 class="text-2xl font-bold">Security</h1>
		</div>
		<p class="text-sm text-muted-foreground">Manage your password and sign-in security.</p>
	</div>

	<Card.Root class="max-w-xl gap-0 py-0" data-testid="security-change-password-card">
		<Card.Content class="p-5">
			<p class="mb-1 text-sm font-semibold">Change password</p>
			<p class="mb-4 text-xs text-muted-foreground">
				Pick a strong password you don't use anywhere else. If you sign in with Google, there's no
				password to change here.
			</p>

			<form
				method="POST"
				action="?/changePassword"
				novalidate
				data-testid="security-change-password-form"
				use:enhance={() => {
					submitting = true;
					return async ({ update }) => {
						await update({ reset: false });
						submitting = false;
					};
				}}
				class="space-y-4"
			>
				<!-- Hidden username field helps password managers associate the new credential. -->
				<input
					type="text"
					name="username"
					autocomplete="username"
					tabindex="-1"
					aria-hidden="true"
					class="sr-only"
					value=""
					data-testid="security-username"
				/>

				<div>
					<label for="currentPassword" class="mb-1.5 block text-sm font-medium text-foreground">
						Current password
					</label>
					<div class="relative">
						<Input
							id="currentPassword"
							name="currentPassword"
							type={showCurrent ? 'text' : 'password'}
							autocomplete="current-password"
							bind:value={currentPassword}
							required
							placeholder="Enter current password"
							class="h-11 pr-10"
							data-testid="security-current-password"
						/>
						<button
							type="button"
							class="absolute right-2 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground"
							aria-label={showCurrent ? 'Hide current password' : 'Show current password'}
							onclick={() => (showCurrent = !showCurrent)}
							data-testid="security-toggle-current-password"
						>
							{#if showCurrent}<EyeOff class="h-4 w-4" />{:else}<Eye class="h-4 w-4" />{/if}
						</button>
					</div>
				</div>

				<div>
					<label for="newPassword" class="mb-1.5 block text-sm font-medium text-foreground">
						New password
					</label>
					<div class="relative">
						<Input
							id="newPassword"
							name="newPassword"
							type={showNew ? 'text' : 'password'}
							autocomplete="new-password"
							bind:value={newPassword}
							required
							placeholder="At least 8 characters"
							class="h-11 pr-10"
							data-testid="security-new-password"
						/>
						<button
							type="button"
							class="absolute right-2 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground"
							aria-label={showNew ? 'Hide new password' : 'Show new password'}
							onclick={() => (showNew = !showNew)}
							data-testid="security-toggle-new-password"
						>
							{#if showNew}<EyeOff class="h-4 w-4" />{:else}<Eye class="h-4 w-4" />{/if}
						</button>
					</div>

					{#if newPassword.length > 0}
						<ul class="mt-1.5 space-y-0.5 text-xs" data-testid="security-password-rules">
							<li class="flex items-center gap-1.5 {hasLength ? 'text-emerald-600 dark:text-emerald-400' : 'text-muted-foreground'}">
								{#if hasLength}<Check class="h-3 w-3" />{:else}<X class="h-3 w-3" />{/if}
								At least 8 characters
							</li>
							<li class="flex items-center gap-1.5 {hasUpper ? 'text-emerald-600 dark:text-emerald-400' : 'text-muted-foreground'}">
								{#if hasUpper}<Check class="h-3 w-3" />{:else}<X class="h-3 w-3" />{/if}
								One uppercase letter (A-Z)
							</li>
							<li class="flex items-center gap-1.5 {hasLower ? 'text-emerald-600 dark:text-emerald-400' : 'text-muted-foreground'}">
								{#if hasLower}<Check class="h-3 w-3" />{:else}<X class="h-3 w-3" />{/if}
								One lowercase letter (a-z)
							</li>
							<li class="flex items-center gap-1.5 {hasDigit ? 'text-emerald-600 dark:text-emerald-400' : 'text-muted-foreground'}">
								{#if hasDigit}<Check class="h-3 w-3" />{:else}<X class="h-3 w-3" />{/if}
								One number (0-9)
							</li>
						</ul>
					{/if}
				</div>

				<div>
					<label for="confirmPassword" class="mb-1.5 block text-sm font-medium text-foreground">
						Confirm new password
					</label>
					<div class="relative">
						<Input
							id="confirmPassword"
							name="confirmPassword"
							type={showConfirm ? 'text' : 'password'}
							autocomplete="new-password"
							bind:value={confirmPassword}
							required
							placeholder="Re-enter new password"
							class="h-11 pr-10"
							data-testid="security-confirm-password"
						/>
						<button
							type="button"
							class="absolute right-2 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground"
							aria-label={showConfirm ? 'Hide confirmation password' : 'Show confirmation password'}
							onclick={() => (showConfirm = !showConfirm)}
							data-testid="security-toggle-confirm-password"
						>
							{#if showConfirm}<EyeOff class="h-4 w-4" />{:else}<Eye class="h-4 w-4" />{/if}
						</button>
					</div>
					{#if confirmPassword.length > 0 && !passwordsMatch}
						<p class="mt-1 text-xs text-destructive" data-testid="security-confirm-mismatch">
							Passwords do not match.
						</p>
					{/if}
				</div>

				{#if form?.error}
					<div
						class="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
						role="alert"
						data-testid="security-change-password-error"
					>
						{form.error}
					</div>
				{/if}

				{#if form?.changed}
					<div
						class="rounded-md border border-emerald-500/30 bg-emerald-500/10 px-3 py-2 text-sm text-emerald-700 dark:text-emerald-300"
						role="status"
						data-testid="security-change-password-success"
					>
						Password changed successfully.
					</div>
				{/if}

				<Button
					type="submit"
					disabled={!canSubmit}
					class="h-11"
					data-testid="security-change-password-submit"
				>
					{submitting ? 'Saving…' : 'Change password'}
				</Button>
			</form>
		</Card.Content>
	</Card.Root>
</div>
