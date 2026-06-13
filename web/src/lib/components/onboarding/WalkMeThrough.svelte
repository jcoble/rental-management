<script lang="ts">
	import { Wand2 } from '@lucide/svelte';
	import { wizardStepHref, type WizardStepKey } from '$lib/onboarding/wizard-steps';

	/**
	 * "Walk me through this" entry point shown next to a Settings section. Deep-links into the
	 * onboarding wizard at the matching step, carrying `from=settings` so the wizard sends the user
	 * back to Settings when the step is done. The wizard pre-fills the step from current values, so
	 * re-running never clobbers existing data.
	 */
	let {
		step,
		label = 'Walk me through this',
		from = 'settings'
	}: { step: WizardStepKey; label?: string; from?: string } = $props();
</script>

<a
	href={wizardStepHref(step, from)}
	class="m3-state-layer inline-flex items-center gap-1.5 rounded-[var(--m3-shape-full,9999px)] border border-primary/40 px-2.5 py-1 text-xs font-medium text-primary hover:bg-primary/10"
	data-testid={`walk-me-through-${step}`}
>
	<Wand2 class="h-3.5 w-3.5" />
	{label}
</a>
