<script lang="ts">
	import { enhance } from '$app/forms';
	import type { ActionData, PageData } from './$types';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Card from '$lib/components/ui/card';
	import BrandMark from '$lib/components/BrandMark.svelte';

	let { data, form }: { data: PageData; form: ActionData } = $props();
	let submitting = $state(false);
</script>

<svelte:head><title>Activate account - Rental Command</title></svelte:head>

<div class="auth-page-wrap">
	<Card.Root class="w-full max-w-md shadow-xl">
		<Card.Content class="p-6">
			<div class="mb-5 flex items-center gap-2">
				<BrandMark class="h-8 w-8 shadow-sm" />
				<h1 class="text-xl font-bold text-foreground">Rental Command</h1>
			</div>

			{#if data.invalidLink}
				<div class="space-y-4" data-testid="team-activation-invalid">
					<h2 class="font-semibold">Invalid activation link</h2>
					<p class="text-sm text-muted-foreground">
						Ask your workspace administrator to send a new invitation.
					</p>
					<Button href="/login" variant="outline" class="w-full">Back to sign in</Button>
				</div>
			{:else if form && 'activated' in form && form.activated}
				<div class="space-y-4" data-testid="team-activation-success">
					<h2 class="font-semibold">Your account is ready</h2>
					<p class="text-sm text-muted-foreground">
						Your password is set. Sign in to open the workspace and the job your administrator assigned.
					</p>
					<Button href="/login" class="w-full">Sign in</Button>
				</div>
			{:else}
				<h2 class="font-semibold">Activate your account</h2>
				<p class="mb-4 mt-1 text-sm text-muted-foreground">
					Choose your password. Your workspace and job access are already prepared.
				</p>
				<form
					method="POST"
					class="space-y-3"
					data-testid="team-activation-form"
					use:enhance={() => {
						submitting = true;
						return async ({ update }) => {
							await update();
							submitting = false;
						};
					}}
				>
					<input type="hidden" name="token" value={data.token} />
					<label class="block space-y-1 text-sm">
						Password
						<Input name="password" type="password" autocomplete="new-password" required />
					</label>
					<label class="block space-y-1 text-sm">
						Confirm password
						<Input name="confirmPassword" type="password" autocomplete="new-password" required />
					</label>
					{#if form && 'error' in form && form.error}
						<p class="text-sm text-destructive" role="alert">{form.error}</p>
					{/if}
					<Button type="submit" disabled={submitting} class="w-full">
						{submitting ? 'Activating…' : 'Activate account'}
					</Button>
				</form>
			{/if}
		</Card.Content>
	</Card.Root>
</div>
