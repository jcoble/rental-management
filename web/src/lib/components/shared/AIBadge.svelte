<script lang="ts">
	let { class: className = '' }: { class?: string } = $props();
</script>

<span
	class="inline-flex items-center rounded-full bg-gradient-to-r from-purple-500/20 to-blue-500/20 px-1.5 py-0.5 text-[10px] font-medium text-purple-300 ring-1 ring-purple-500/30 {className}"
>
	AI
</span>
