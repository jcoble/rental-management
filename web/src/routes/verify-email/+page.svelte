<script lang="ts">
	import { Building } from '@lucide/svelte';
	import type { PageData } from './$types';
	import { Button } from '$lib/components/ui/button';
	import * as Card from '$lib/components/ui/card';

	let { data }: { data: PageData } = $props();
</script>

<svelte:head>
	<title>Verify email - Rental Command</title>
</svelte:head>

<div class="auth-page-wrap">
	<Card.Root class="w-full max-w-md shadow-xl">
		<Card.Content class="p-6">
			<div class="mb-5 flex items-center gap-2">
				<Building class="h-6 w-6 text-primary" />
				<h1 class="text-xl font-bold text-foreground">Rental Command</h1>
			</div>

			{#if data.success}
				<div class="space-y-4" data-testid="verify-email-success">
					<p class="text-sm font-medium text-foreground">Email verified</p>
					<p class="text-sm text-muted-foreground">
						Your email is verified — you can now sign in.
					</p>
					<Button href="/login" class="w-full">Sign in</Button>
				</div>
			{:else}
				<div class="space-y-4" data-testid="verify-email-error">
					<p class="text-sm font-medium text-foreground">Verification failed</p>
					<p class="text-sm text-muted-foreground" role="alert">
						{data.error ?? 'This verification link is invalid or has expired.'}
					</p>
					<p class="text-sm text-muted-foreground">
						<a href="/login" class="text-primary underline-offset-4 hover:underline">
							Back to sign in
						</a>
					</p>
				</div>
			{/if}
		</Card.Content>
	</Card.Root>
</div>
