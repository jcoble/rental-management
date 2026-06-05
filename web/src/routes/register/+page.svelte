<script lang="ts">
	import { enhance } from '$app/forms';
	import { Building, Loader2, MailCheck, CheckCircle2 } from '@lucide/svelte';
	import type { ActionData, PageData } from './$types';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import AuthBrandPanel from '$lib/components/auth/AuthBrandPanel.svelte';

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

	// Lightweight live validation hints (don't block the submit button — the
	// server is the source of truth — but give friendly inline feedback).
	const passwordTooShort = $derived(password.length > 0 && password.length < 8);
	const passwordsMismatch = $derived(confirmPassword.length > 0 && confirmPassword !== password);

	// Google Sign-In is only available when the server signals it is configured.
	const googleEnabled = $derived(data.googleEnabled);
</script>

<svelte:head>
	<title>Create account - Rental Command</title>
</svelte:head>

<div class="grid h-dvh w-full grid-cols-1 overflow-y-auto bg-background lg:grid-cols-2">
	<AuthBrandPanel tagline="Get your whole portfolio set up in minutes." />

	<div class="flex items-center justify-center p-6 sm:p-10">
		<div class="w-full max-w-sm">
			<!-- Compact brand for narrow screens -->
			<div class="mb-8 flex items-center gap-2 lg:hidden">
				<span
					class="flex h-9 w-9 items-center justify-center rounded-xl bg-primary/10 text-primary ring-1 ring-inset ring-primary/20"
				>
					<Building class="h-5 w-5" />
				</span>
				<span class="text-lg font-semibold tracking-tight text-foreground">Rental Command</span>
			</div>

			{#if form && 'registered' in form && form.registered}
				<!-- Success: email sent -->
				<div class="space-y-5" data-testid="register-success">
					<div
						class="flex h-12 w-12 items-center justify-center rounded-full bg-green-500/10 text-green-500 ring-1 ring-inset ring-green-500/20"
					>
						<MailCheck class="h-6 w-6" />
					</div>
					<div class="space-y-1.5">
						<h1 class="text-2xl font-semibold tracking-tight text-foreground">Check your email</h1>
						<p class="text-sm text-muted-foreground">
							We sent a verification link to
							<span class="font-medium text-foreground">{form.email}</span>. Click it to activate your
							account.
						</p>
						<p class="text-sm text-muted-foreground">
							Don't see it? Check your <span class="font-medium text-foreground">spam folder</span> — it
							can take a minute to arrive.
						</p>
					</div>
					<a
						href="/login"
						class="inline-block text-sm font-medium text-primary underline-offset-4 hover:underline"
					>
						Back to sign in
					</a>
				</div>
			{:else}
				<div class="mb-6">
					<h1 class="text-2xl font-semibold tracking-tight text-foreground">Create your account</h1>
					<p class="mt-1 text-sm text-muted-foreground">Start managing your rentals in minutes.</p>
				</div>

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
					class="space-y-4"
				>
					<div>
						<label for="register-displayname" class="mb-1.5 block text-sm font-medium text-foreground">
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
							class="h-11"
						/>
					</div>

					<div>
						<label for="register-email" class="mb-1.5 block text-sm font-medium text-foreground">
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
							class="h-11"
						/>
					</div>

					<div>
						<label for="register-password" class="mb-1.5 block text-sm font-medium text-foreground">
							Password
							<span class="font-normal text-muted-foreground">(min. 8 characters)</span>
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
							class="h-11"
							aria-invalid={passwordTooShort}
						/>
						{#if passwordTooShort}
							<p class="mt-1 text-xs text-amber-500">Use at least 8 characters.</p>
						{/if}
					</div>

					<div>
						<label
							for="register-confirm-password"
							class="mb-1.5 block text-sm font-medium text-foreground"
						>
							Confirm password
						</label>
						<div class="relative">
							<Input
								id="register-confirm-password"
								name="confirmPassword"
								type="password"
								data-testid="register-confirm-password-input"
								autocomplete="new-password"
								bind:value={confirmPassword}
								required
								placeholder="••••••••"
								class="h-11 pr-9"
								aria-invalid={passwordsMismatch}
							/>
							{#if confirmPassword.length > 0 && !passwordsMismatch}
								<CheckCircle2
									class="absolute right-3 top-1/2 h-4 w-4 -translate-y-1/2 text-green-500"
									aria-hidden="true"
								/>
							{/if}
						</div>
						{#if passwordsMismatch}
							<p class="mt-1 text-xs text-destructive">Passwords don't match.</p>
						{/if}
					</div>

					{#if form && 'error' in form && form.error}
						<div
							role="alert"
							data-testid="register-error"
							class="space-y-1 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2"
						>
							<p class="text-sm text-destructive">{form.error}</p>
							{#if form.details}
								<p class="text-xs text-muted-foreground">{form.details}</p>
							{/if}
						</div>
					{/if}

					<Button
						type="submit"
						data-testid="register-submit"
						disabled={submitting}
						class="h-11 w-full"
					>
						{#if submitting}
							<Loader2 class="h-4 w-4 animate-spin" />
							Creating account…
						{:else}
							Create account
						{/if}
					</Button>

					{#if googleEnabled}
						<div class="relative flex items-center py-1">
							<div class="flex-grow border-t border-border"></div>
							<span class="mx-3 flex-shrink text-xs text-muted-foreground">or continue with</span>
							<div class="flex-grow border-t border-border"></div>
						</div>

						<a href="/auth/google" class="auth-google-button h-11" data-testid="register-google-button">
							<svg class="h-4 w-4" viewBox="0 0 24 24" aria-hidden="true">
								<path
									d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92c-.26 1.37-1.04 2.53-2.21 3.31v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.09z"
									fill="#4285F4"
								/>
								<path
									d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z"
									fill="#34A853"
								/>
								<path
									d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.07H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.93l2.85-2.22.81-.62z"
									fill="#FBBC05"
								/>
								<path
									d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84c.87-2.6 3.3-4.53 6.16-4.53z"
									fill="#EA4335"
								/>
							</svg>
							Sign up with Google
						</a>
					{/if}

					<p class="text-center text-sm text-muted-foreground">
						Already have an account?
						<a href="/login" class="font-medium text-primary underline-offset-4 hover:underline">Sign in</a>
					</p>
				</form>
			{/if}
		</div>
	</div>
</div>
