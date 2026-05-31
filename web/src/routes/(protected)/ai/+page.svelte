<script lang="ts">
	// AI features (intake triage, portfolio risk summary, notice drafting) are a Phase 3
	// deliverable. The backend endpoints (/ai/intake, /ai/portfolio-summary, /ai/generate-notice)
	// don't exist yet, so this page renders a coming-soon empty-state instead of calling them.
	// The client methods in $lib/api/endpoints/ai.ts are left in place for Phase 3 to wire up.
	import { Sparkles } from '@lucide/svelte';
	import * as Card from '$lib/components/ui/card';
</script>

<svelte:head>
	<title>AI Assistant - Rental Command</title>
</svelte:head>

<div class="flex h-full items-center justify-center p-6" data-testid="ai-page">
	<Card.Root class="max-w-md gap-0 py-0 text-center" data-testid="ai-coming-soon">
		<Card.Content class="p-8">
			<div class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10 text-primary">
				<Sparkles class="h-6 w-6" />
			</div>
			<h1 class="text-xl font-bold">AI Assistant</h1>
			<p class="mt-2 text-sm text-muted-foreground">
				AI features are coming in a later phase. Intake triage, portfolio risk summaries, and notice
				drafting will live here once they're ready.
			</p>
		</Card.Content>
	</Card.Root>
</div>
