<script lang="ts">
	import { page } from '$app/state';
	import { Shield, UserRound } from '@lucide/svelte';
</script>

<svelte:head><title>Profile - Rental Command</title></svelte:head>

<div class="mx-auto w-full max-w-3xl space-y-6 p-4 sm:p-6">
	<header><p class="text-sm text-muted-foreground">Maintenance technician</p><h1 class="mt-1 text-2xl font-semibold">Profile</h1><p class="mt-2 text-sm text-muted-foreground">Your sign-in and work identity.</p></header>
	<section class="rounded-2xl border border-border bg-card p-5">
		<div class="flex items-center gap-4"><div class="rounded-full bg-primary/15 p-3 text-primary"><UserRound class="h-6 w-6" /></div><div><h2 class="font-semibold">{page.data.user?.displayName ?? 'Technician'}</h2><p class="text-sm text-muted-foreground">{page.data.user?.email ?? ''}</p></div></div>
	</section>
	<a href="/settings/security" class="flex items-center justify-between rounded-2xl border border-border bg-card p-5 transition hover:border-primary/50"><span class="flex items-center gap-3"><Shield class="h-5 w-5 text-primary" /><span><span class="block font-medium">Sign-in security</span><span class="block text-sm text-muted-foreground">Password and active sessions</span></span></span><span aria-hidden="true">›</span></a>
</div>
