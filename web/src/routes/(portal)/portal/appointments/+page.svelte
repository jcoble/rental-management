<script lang="ts">
	import { CalendarClock } from '@lucide/svelte';
</script>

<svelte:head><title>Appointments - Rental Command</title></svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-5 flex items-center gap-2"><CalendarClock class="h-5 w-5 text-primary" /><h1 class="text-2xl font-semibold">Appointments</h1></div>
	<div class="rounded-lg border border-border bg-card p-4">
		<p class="text-sm text-muted-foreground">Upcoming visits, inspections, and maintenance appointments will appear here.</p>
	</div>
</div>
