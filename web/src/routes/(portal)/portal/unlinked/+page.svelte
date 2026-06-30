<script lang="ts">
	// Shown to a signed-in Tenant-role user whose account isn't linked to a tenant record yet. The
	// (portal) layout guard routes them here instead of a dashboard that would 403 on every card.
	import { UserRoundX } from '@lucide/svelte';
</script>

<svelte:head>
	<title>Account not linked - Rental Command</title>
</svelte:head>

<div class="flex h-full items-center justify-center overflow-y-auto p-6" data-testid="portal-unlinked">
	<div class="w-full max-w-md rounded-xl border border-border bg-card p-8 text-center">
		<div class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-muted">
			<UserRoundX class="h-6 w-6 text-muted-foreground" />
		</div>
		<h1 class="text-xl font-semibold" data-testid="portal-unlinked-title">
			Your account isn’t linked yet
		</h1>
		<p class="mt-2 text-sm leading-6 text-muted-foreground">
			You’re signed in, but your account isn’t connected to a tenant record yet — so there’s
			nothing to show in your portal. Please contact your landlord to finish setting up your access.
		</p>
	</div>
</div>
