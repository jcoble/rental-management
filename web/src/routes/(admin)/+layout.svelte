<script lang="ts">
	import AppShell from '$lib/components/AppShell.svelte';

	let { children }: { children: import('svelte').Snippet } = $props();
</script>

<AppShell {children} />
