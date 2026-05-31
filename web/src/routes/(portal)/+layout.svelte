<script lang="ts">
	// The portal is a self-contained, full-page experience for owners/tenants —
	// no staff app shell. The guard lives in +layout.server.ts.
	let { children }: { children: import('svelte').Snippet } = $props();
</script>

<main class="h-screen overflow-y-auto bg-bg">
	{@render children()}
</main>
