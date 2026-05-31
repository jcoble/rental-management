<script lang="ts">
	import { enhance } from '$app/forms';
	import { Building } from '@lucide/svelte';
	import type { ActionData, PageData } from './$types';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Card from '$lib/components/ui/card';

	let { data, form }: { data: PageData; form: ActionData } = $props();

	let submitting = $state(false);
	let displayName = $state('');
	let email = $state('');
	let password = $state('');
	let confirmPassword = $state('');

	// Repopulate fields after a failed submit.
	$effect(() => {
		if (form && 'error' in form) {
			if (form.displayName) displayName = form.displayName ?? '';
			if (form.email) email = form.email ?? '';
		}
	});
</script>

<svelte:head>
	<title>Create account - Rental Command</title>
</svelte:head>

<div class="flex h-full items-center justify-center bg-background p-6">
	<Card.Root class="w-full max-w-md shadow-xl">
		<Card.Content class="p-6">
			<div class="mb-5 flex items-center gap-2">
				<Building class="h-6 w-6 text-primary" />
				<h1 class="text-xl font-bold text-foreground">Rental Command</h1>
			</div>

			{#if form && 'registered' in form && form.registered}
				<!-- Success: email sent -->
				<div class="space-y-4" data-testid="register-success">
					<p class="text-sm font-medium text-foreground">Check your email</p>
					<p class="text-sm text-muted-foreground">
						We sent a verification link to <span class="font-medium text-foreground">{form.email}</span>.
						Click the link in that email to activate your account.
					</p>
					<a
						href="/login"
						class="inline-block text-sm text-primary underline-offset-4 hover:underline"
					>
						Back to sign in
					</a>
				</div>
			{:else}
				<p class="mb-4 text-sm text-muted-foreground">Create your account.</p>

				<form
					method="POST"
					data-testid="register-form"
					use:enhance={() => {
						submitting = true;
						return async ({ update }) => {
							await update();
							submitting = false;
						};
					}}
					class="space-y-3"
				>
					<div>
						<label for="register-displayname" class="mb-1 block text-xs text-muted-foreground">
							Display name
						</label>
						<Input
							id="register-displayname"
							name="displayName"
							type="text"
							data-testid="register-displayname-input"
							autocomplete="name"
							bind:value={displayName}
							required
							placeholder="Jane Smith"
						/>
					</div>

					<div>
						<label for="register-email" class="mb-1 block text-xs text-muted-foreground">
							Email
						</label>
						<Input
							id="register-email"
							name="email"
							type="email"
							data-testid="register-email-input"
							autocomplete="email"
							bind:value={email}
							required
							placeholder="you@example.com"
						/>
					</div>

					<div>
						<label for="register-password" class="mb-1 block text-xs text-muted-foreground">
							Password <span class="text-muted-foreground/60">(min. 8 characters)</span>
						</label>
						<Input
							id="register-password"
							name="password"
							type="password"
							data-testid="register-password-input"
							autocomplete="new-password"
							bind:value={password}
							required
							placeholder="••••••••"
						/>
					</div>

					<div>
						<label for="register-confirm-password" class="mb-1 block text-xs text-muted-foreground">
							Confirm password
						</label>
						<Input
							id="register-confirm-password"
							name="confirmPassword"
							type="password"
							data-testid="register-confirm-password-input"
							autocomplete="new-password"
							bind:value={confirmPassword}
							required
							placeholder="••••••••"
						/>
					</div>

					<Button
						type="submit"
						data-testid="register-submit"
						disabled={submitting}
						class="w-full"
					>
						{submitting ? 'Creating account…' : 'Create account'}
					</Button>

					{#if form && 'error' in form && form.error}
						<div role="alert" data-testid="register-error" class="space-y-1">
							<p class="text-sm text-destructive">{form.error}</p>
							{#if form.details}
								<p class="text-xs text-muted-foreground">{form.details}</p>
							{/if}
						</div>
					{/if}

					<p class="text-center text-xs text-muted-foreground">
						Already have an account?
						<a href="/login" class="text-primary underline-offset-4 hover:underline">Sign in</a>
					</p>
				</form>
			{/if}
		</Card.Content>
	</Card.Root>
</div>
