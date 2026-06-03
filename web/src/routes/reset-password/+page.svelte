<script lang="ts">
	import { enhance } from '$app/forms';
	import { Building } from '@lucide/svelte';
	import type { ActionData, PageData } from './$types';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Card from '$lib/components/ui/card';

	let { data, form }: { data: PageData; form: ActionData } = $props();

	let submitting = $state(false);
	let newPassword = $state('');
	let confirmPassword = $state('');
</script>

<svelte:head>
	<title>Reset password - Rental Command</title>
</svelte:head>

<div class="auth-page-wrap">
	<Card.Root class="w-full max-w-md shadow-xl">
		<Card.Content class="p-6">
			<div class="mb-5 flex items-center gap-2">
				<Building class="h-6 w-6 text-primary" />
				<h1 class="text-xl font-bold text-foreground">Rental Command</h1>
			</div>

			{#if data.invalidLink}
				<div class="space-y-4" data-testid="reset-password-invalid">
					<p class="text-sm font-medium text-foreground">Invalid reset link</p>
					<p class="text-sm text-muted-foreground">
						This password reset link is invalid or has expired.
					</p>
					<div class="flex gap-2">
						<a
							href="/forgot-password"
							class="text-sm text-primary underline-offset-4 hover:underline"
						>
							Request a new link
						</a>
						<span class="text-muted-foreground">·</span>
						<a href="/login" class="text-sm text-primary underline-offset-4 hover:underline">
							Sign in
						</a>
					</div>
				</div>
			{:else if form && 'reset' in form && form.reset}
				<!-- Success state -->
				<div class="space-y-4" data-testid="reset-password-success">
					<p class="text-sm font-medium text-foreground">Password updated</p>
					<p class="text-sm text-muted-foreground">
						Your password has been reset. You can now sign in with your new password.
					</p>
					<Button href="/login" class="w-full">Sign in</Button>
				</div>
			{:else}
				<p class="mb-4 text-sm text-muted-foreground">Enter your new password below.</p>

				<form
					method="POST"
					data-testid="reset-password-form"
					use:enhance={() => {
						submitting = true;
						return async ({ update }) => {
							await update();
							submitting = false;
						};
					}}
					class="space-y-3"
				>
					<!-- Hidden fields carry the link tokens through form submission -->
					<input type="hidden" name="userId" value={data.userId} />
					<input type="hidden" name="token" value={data.token} />

					<div>
						<label for="reset-new-password" class="mb-1 block text-xs text-muted-foreground">
							New password <span class="text-muted-foreground/60">(min. 8 characters)</span>
						</label>
						<Input
							id="reset-new-password"
							name="newPassword"
							type="password"
							data-testid="reset-password-new-input"
							autocomplete="new-password"
							bind:value={newPassword}
							required
							placeholder="••••••••"
						/>
					</div>

					<div>
						<label for="reset-confirm-password" class="mb-1 block text-xs text-muted-foreground">
							Confirm new password
						</label>
						<Input
							id="reset-confirm-password"
							name="confirmPassword"
							type="password"
							data-testid="reset-password-confirm-input"
							autocomplete="new-password"
							bind:value={confirmPassword}
							required
							placeholder="••••••••"
						/>
					</div>

					<Button
						type="submit"
						data-testid="reset-password-submit"
						disabled={submitting}
						class="w-full"
					>
						{submitting ? 'Resetting…' : 'Reset password'}
					</Button>

					{#if form && 'error' in form && form.error}
						<p class="text-sm text-destructive" role="alert" data-testid="reset-password-error">
							{form.error}
						</p>
					{/if}

					<p class="text-center text-xs text-muted-foreground">
						<a href="/login" class="text-primary underline-offset-4 hover:underline">
							Back to sign in
						</a>
					</p>
				</form>
			{/if}
		</Card.Content>
	</Card.Root>
</div>
