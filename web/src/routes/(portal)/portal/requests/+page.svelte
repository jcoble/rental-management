<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { portal } from '$lib/api/endpoints/portal';
	import { ClipboardList } from '@lucide/svelte';

	const conversationsQuery = createQuery(() => ({ queryKey: ['portal-requests-page'], queryFn: () => portal.conversations.list() }));
</script>

<svelte:head><title>Requests - Rental Command</title></svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-5 flex items-center gap-2"><ClipboardList class="h-5 w-5 text-primary" /><h1 class="text-2xl font-semibold">Requests</h1></div>
	<div class="space-y-3">
		{#each conversationsQuery.data ?? [] as conversation}
			<a href={`/portal/messages?conversation=${conversation.id}`} class="block rounded-lg border border-border bg-card p-4 hover:bg-muted/40">
				<p class="font-medium">{conversation.subject}</p>
				<p class="mt-1 text-sm text-muted-foreground">{conversation.lastMessagePreview}</p>
			</a>
		{:else}
			<p class="text-sm text-muted-foreground">No requests yet.</p>
		{/each}
	</div>
</div>
