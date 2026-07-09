<script lang="ts">
	import { enhance } from '$app/forms';
	import type { ActionData, PageData } from './$types';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Card from '$lib/components/ui/card';
	import BrandMark from '$lib/components/BrandMark.svelte';

	let { data, form }: { data: PageData; form: ActionData } = $props();

	let submitting = $state(false);
	let email = $state('');
</script>

<svelte:head>
	<title>Forgot password - Rental Command</title>
</svelte:head>

<div class="auth-page-wrap">
	<Card.Root class="w-full max-w-md shadow-xl">
		<Card.Content class="p-6">
			<div class="mb-5 flex items-center gap-2">
				<BrandMark class="h-8 w-8 shadow-sm" />
				<h1 class="text-xl font-bold text-foreground">Rental Command</h1>
			</div>

			{#if form && 'sent' in form && form.sent}
				<!-- Generic success — never mention whether the account exists. -->
				<div class="space-y-4" data-testid="forgot-password-success">
					<p class="text-sm font-medium text-foreground">Check your email</p>
					<p class="text-sm text-muted-foreground">
						If an account exists for that email, we've sent a password reset link.
					</p>
					<a
						href="/login"
						class="inline-block text-sm text-primary underline-offset-4 hover:underline"
					>
						Back to sign in
					</a>
				</div>
			{:else}
				<p class="mb-4 text-sm text-muted-foreground">
					Enter your email and we'll send you a reset link.
				</p>

				<form
					method="POST"
					data-testid="forgot-password-form"
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
						<label for="forgot-email" class="mb-1 block text-xs text-muted-foreground">Email</label>
						<Input
							id="forgot-email"
							name="email"
							type="email"
							data-testid="forgot-password-email-input"
							autocomplete="email"
							bind:value={email}
							required
							placeholder="you@example.com"
						/>
					</div>

					<Button
						type="submit"
						data-testid="forgot-password-submit"
						disabled={submitting}
						class="w-full"
					>
						{submitting ? 'Sending…' : 'Send reset link'}
					</Button>

					{#if form && 'error' in form && form.error}
						<p class="text-sm text-destructive" role="alert" data-testid="forgot-password-error">
							{form.error}
						</p>
					{/if}

					<p class="text-center text-xs text-muted-foreground">
						<a href="/login" class="text-primary underline-offset-4 hover:underline">Back to sign in</a>
					</p>
				</form>
			{/if}
		</Card.Content>
	</Card.Root>
</div>
