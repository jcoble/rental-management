<script lang="ts">
	import { enhance } from '$app/forms';
	import { Building } from '@lucide/svelte';
	import type { ActionData, PageData } from './$types';

	let { data, form }: { data: PageData; form: ActionData } = $props();

	let submitting = $state(false);

	// Field values are bound so the dev quick-fill button below can populate them.
	let email = $state('');
	let password = $state('');

	// Repopulate the email after a failed submit (the action echoes it back).
	$effect(() => {
		if (form?.email) email = form.email;
	});

	// Dev-only convenience: prefill the seeded admin so manual testing doesn't
	// require retyping credentials. Stripped from production builds via the DEV flag.
	const isDev = import.meta.env.DEV;
	function fillDevCredentials() {
		email = 'admin@rentalcommand.local';
		password = 'Admin123!';
	}
</script>

<svelte:head>
	<title>Sign in - Rental Command</title>
</svelte:head>

<div class="flex h-full items-center justify-center bg-background p-6">
	<div class="w-full max-w-md rounded-xl border border-border bg-card p-6 shadow-xl">
		<div class="mb-5 flex items-center gap-2">
			<Building class="h-6 w-6 text-primary" />
			<h1 class="text-xl font-bold text-foreground">Rental Command</h1>
		</div>
		<p class="mb-4 text-sm text-muted-foreground">Sign in to your account.</p>

		<form
			method="POST"
			data-testid="login-form"
			use:enhance={() => {
				submitting = true;
				return async ({ update }) => {
					await update();
					submitting = false;
				};
			}}
			class="space-y-3"
		>
			{#if data.redirectTo}
				<input type="hidden" name="redirectTo" value={data.redirectTo} />
			{/if}

			<div>
				<label for="login-email" class="mb-1 block text-xs text-muted-foreground">Email</label>
				<input
					id="login-email"
					name="email"
					type="email"
					data-testid="login-email-input"
					autocomplete="email"
					bind:value={email}
					required
					class="w-full rounded border border-border bg-background px-3 py-2 text-sm text-foreground"
					placeholder="you@example.com"
				/>
			</div>

			<div>
				<label for="login-password" class="mb-1 block text-xs text-muted-foreground">Password</label>
				<input
					id="login-password"
					name="password"
					type="password"
					data-testid="login-password-input"
					autocomplete="current-password"
					bind:value={password}
					required
					class="w-full rounded border border-border bg-background px-3 py-2 text-sm text-foreground"
					placeholder="••••••••"
				/>
			</div>

			<button
				type="submit"
				data-testid="login-submit"
				disabled={submitting}
				class="w-full rounded bg-primary px-3 py-2 text-sm font-medium text-white transition-opacity disabled:opacity-60"
			>
				{submitting ? 'Signing in…' : 'Sign In'}
			</button>

			{#if isDev}
				<button
					type="button"
					data-testid="login-fill-dev"
					onclick={fillDevCredentials}
					class="w-full rounded border border-dashed border-border px-3 py-2 text-xs text-muted-foreground transition-colors hover:text-muted-foreground"
				>
					Fill dev login (admin)
				</button>
			{/if}

			{#if form?.error}
				<p class="text-sm text-destructive" role="alert" data-testid="login-error">{form.error}</p>
			{/if}
		</form>
	</div>
</div>
