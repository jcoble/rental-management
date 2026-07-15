<script lang="ts">
	import { enhance } from '$app/forms';
	import { Loader2 } from '@lucide/svelte';
	import type { ActionData, PageData } from './$types';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import AuthBrandPanel from '$lib/components/auth/AuthBrandPanel.svelte';
	import BrandMark from '$lib/components/BrandMark.svelte';
	import { auth } from '$lib/api/endpoints/auth';
	import type { EffectiveAccessContextOption } from '$lib/types/user';
	let { data, form }: { data: PageData; form: ActionData } = $props();

	let submitting = $state(false);
	let submissionError = $state('');

	// Field values are bound so the dev quick-fill button below can populate them.
	let email = $state('');
	let password = $state('');
	let accessContextId = $state('');
	const contextChoices: EffectiveAccessContextOption[] = $derived(
		form && 'contexts' in form && Array.isArray(form.contexts) ? form.contexts : []
	);

	// "Resend verification" affordance, shown only when the API flags the login as EMAIL_NOT_VERIFIED.
	let resendingVerification = $state(false);
	let resendSuccess = $state(false);
	const emailNotVerified = $derived(form?.emailNotVerified === true);
	const visibleLoginError = $derived(submissionError || form?.error || '');

	// Repopulate the email after a failed submit (the action echoes it back).
	$effect(() => {
		if (form?.email) email = form.email;
	});

	// Re-send the verification email for the address in the box. The endpoint is anonymous and always
	// returns a neutral success (no account enumeration), so we treat any non-throw as "sent".
	async function handleResendVerification() {
		if (!email) return;
		resendingVerification = true;
		resendSuccess = false;
		try {
			await auth.resendVerification(email);
			resendSuccess = true;
		} catch {
			// Silent — the API intentionally hides whether the account exists.
		} finally {
			resendingVerification = false;
		}
	}

	// Dev-only convenience: prefill the seeded admin so manual testing doesn't
	// require retyping credentials. Stripped from production builds via the DEV flag.
	const isDev = import.meta.env.DEV;
	function fillDevCredentials() {
		email = 'admin@rentalcommand.local';
		password = 'Admin123!';
	}

	// Google Sign-In is only available when the server signals it is configured.
	const googleEnabled = $derived(data.googleEnabled);
</script>

<svelte:head>
	<title>Sign in - Rental Command</title>
</svelte:head>

<div class="grid h-dvh w-full grid-cols-1 overflow-y-auto bg-background lg:grid-cols-2">
	<AuthBrandPanel />

	<div class="flex items-center justify-center p-6 sm:p-10">
		<div class="w-full max-w-sm">
			<!-- Compact brand for narrow screens (the split panel is hidden there) -->
			<div class="mb-8 flex items-center gap-2 lg:hidden">
				<BrandMark class="h-9 w-9 shadow-sm" />
				<span class="text-lg font-semibold tracking-tight text-foreground">Rental Command</span>
			</div>

			<div class="mb-6">
				<h1 class="text-2xl font-semibold tracking-tight text-foreground">Welcome back</h1>
				<p class="mt-1 text-sm text-muted-foreground">Sign in to your account to continue.</p>
			</div>

			<form
				method="POST"
				data-testid="login-form"
				use:enhance={() => {
					submitting = true;
					submissionError = '';
					return async ({ result, update }) => {
						try {
							if (result.type === 'error') {
								submissionError =
									'Rental Command could not complete sign-in. Please try again or contact support.';
								return;
							}
							await update();
						} catch {
							submissionError =
								'Rental Command could not complete sign-in. Please try again or contact support.';
						} finally {
							submitting = false;
						}
					};
				}}
				class="space-y-4"
			>
				{#if data.redirectTo}
					<input type="hidden" name="redirectTo" value={data.redirectTo} />
				{/if}
				{#if contextChoices.length > 1}
					<div class="rounded-lg border border-border bg-card/60 p-3">
						<label for="login-context" class="mb-1.5 block text-sm font-medium text-foreground">
							Which workspace do you want to open?
						</label>
						<select
							id="login-context"
							name="accessContextId"
							bind:value={accessContextId}
							required
							class="h-11 w-full rounded-md border border-input bg-background px-3 text-sm"
						>
							<option value="" disabled>Select a workspace</option>
							{#each contextChoices as context}
								<option value={context.accessContextId}>{context.workspaceName}</option>
							{/each}
						</select>
						<p class="mt-1.5 text-xs text-muted-foreground">
							You can switch workspaces later from the app menu.
						</p>
					</div>
				{/if}

				<div>
					<label for="login-email" class="mb-1.5 block text-sm font-medium text-foreground">Email</label>
					<Input
						id="login-email"
						name="email"
						type="email"
						data-testid="login-email-input"
						autocomplete="email"
						bind:value={email}
						required
						placeholder="you@example.com"
						class="h-11"
					/>
				</div>

				<div>
					<div class="mb-1.5 flex items-center justify-between">
						<label for="login-password" class="block text-sm font-medium text-foreground">Password</label>
						<a
							href="/forgot-password"
							class="text-xs font-medium text-primary underline-offset-4 hover:underline"
							data-testid="login-forgot-password-link"
						>
							Forgot password?
						</a>
					</div>
					<Input
						id="login-password"
						name="password"
						type="password"
						data-testid="login-password-input"
						autocomplete="current-password"
						bind:value={password}
						required
						placeholder="••••••••"
						class="h-11"
					/>
				</div>

				{#if visibleLoginError}
					{#if emailNotVerified}
						<div
							class="rounded-md border border-amber-500/30 bg-amber-500/10 px-3 py-3 text-sm"
							role="alert"
							data-testid="login-email-not-verified"
						>
							<p class="font-medium text-amber-700 dark:text-amber-200">
								Please verify your email address before signing in.
							</p>
							<p class="mt-1 text-amber-700/80 dark:text-amber-200/80">
								Check your inbox for a verification link.
							</p>
							{#if resendSuccess}
								<p
									class="mt-2 font-medium text-emerald-600 dark:text-emerald-400"
									data-testid="login-resend-success"
								>
									Verification email sent! Check your inbox.
								</p>
							{:else}
								<button
									type="button"
									onclick={handleResendVerification}
									disabled={resendingVerification || !email}
									class="mt-2 text-sm font-medium text-amber-700 underline underline-offset-4 hover:no-underline disabled:opacity-50 dark:text-amber-200"
									data-testid="login-resend-verification-btn"
								>
									{resendingVerification ? 'Sending…' : 'Resend verification email'}
								</button>
							{/if}
							<p class="mt-1 text-xs text-amber-700/70 dark:text-amber-200/60">
								Check your spam folder if you don't see it.
							</p>
						</div>
					{:else}
						<div
							class="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
							role="alert"
							aria-live="polite"
							data-testid="login-error"
						>
							{visibleLoginError}
						</div>
					{/if}
				{/if}

				<Button type="submit" data-testid="login-submit" disabled={submitting} class="h-11 w-full">
					{#if submitting}
						<Loader2 class="h-4 w-4 animate-spin" />
						Signing in…
					{:else}
						Sign In
					{/if}
				</Button>

				{#if data.googleError}
					<p class="text-center text-xs text-muted-foreground" role="alert" data-testid="login-google-error">
						Google Sign-In is not available right now.
					</p>
				{/if}

				{#if googleEnabled}
					<div class="relative flex items-center py-1">
						<div class="flex-grow border-t border-border"></div>
						<span class="mx-3 flex-shrink text-xs text-muted-foreground">or continue with</span>
						<div class="flex-grow border-t border-border"></div>
					</div>

					<a href="/auth/google" class="auth-google-button h-11" data-testid="login-google-button">
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
						Sign in with Google
					</a>
				{/if}

				{#if isDev}
					<Button
						type="button"
						variant="outline"
						data-testid="login-fill-dev"
						onclick={fillDevCredentials}
						class="w-full border-dashed text-xs text-muted-foreground hover:text-muted-foreground"
					>
						Fill dev login (admin)
					</Button>
				{/if}

				<p class="text-center text-sm text-muted-foreground">
					Don't have an account?
					<a href="/register" class="font-medium text-primary underline-offset-4 hover:underline">Create one</a>
				</p>
			</form>
		</div>
	</div>
</div>
