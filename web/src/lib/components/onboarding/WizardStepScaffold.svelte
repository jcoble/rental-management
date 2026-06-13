<script lang="ts">
	import { Building, UserCircle2, Home, Users, FileText, Bell, MessageSquare, HelpCircle, ExternalLink, ChevronDown } from '@lucide/svelte';
	import type { Snippet } from 'svelte';
	import type { WizardStepMeta } from '$lib/onboarding/wizard-steps';
	import { wizardDocsHref } from '$lib/onboarding/wizard-steps';

	type Props = {
		step: WizardStepMeta;
		/** Optional photo pre-fill action (only the lease step provides this today). */
		photoPrefill?: Snippet;
		/** The 1–2 field inputs for this step. */
		children: Snippet;
	};

	let { step, photoPrefill, children }: Props = $props();

	const ICONS = { Building, UserCircle2, Home, Users, FileText, Bell, MessageSquare } as const;
	const Icon = $derived(ICONS[step.icon]);

	// "Where do I find this?" starts collapsed so the step stays calm; one tap reveals the hint.
	let showWhere = $state(false);
</script>

<div data-testid={`onboarding-step-${step.key}`}>
	<div class="mb-3 flex items-center gap-2">
		<span class="flex h-9 w-9 items-center justify-center rounded-[var(--m3-shape-large,0.75rem)] bg-primary/10 text-primary">
			<Icon class="h-5 w-5" />
		</span>
		<h2 class="text-lg font-semibold">{step.title}</h2>
	</div>

	<!-- Plain-English: WHAT this is and WHY it's needed. -->
	<p class="mb-3 text-sm leading-relaxed text-muted-foreground" data-testid={`onboarding-explain-${step.key}`}>
		{step.explanation}
	</p>

	<!-- "Where do I find this?" — collapsible, with a consistent docs link. -->
	<div class="mb-5 rounded-[var(--m3-shape-large,0.75rem)] border border-border bg-muted/40">
		<button
			type="button"
			class="m3-state-layer flex w-full items-center justify-between gap-2 rounded-[var(--m3-shape-large,0.75rem)] px-3 py-2 text-left"
			aria-expanded={showWhere}
			data-testid={`onboarding-where-toggle-${step.key}`}
			onclick={() => (showWhere = !showWhere)}
		>
			<span class="flex items-center gap-2 text-sm font-medium text-foreground">
				<HelpCircle class="h-4 w-4 text-primary" />
				Where do I find this?
			</span>
			<ChevronDown class="h-4 w-4 text-muted-foreground transition-transform {showWhere ? 'rotate-180' : ''}" />
		</button>
		{#if showWhere}
			<div class="border-t border-border px-3 py-2.5" data-testid={`onboarding-where-body-${step.key}`}>
				<p class="text-sm leading-relaxed text-muted-foreground">{step.whereToFind}</p>
				<a
					href={wizardDocsHref(step)}
					target="_blank"
					rel="noopener"
					class="mt-2 inline-flex items-center gap-1 text-xs font-semibold text-primary underline-offset-4 hover:underline"
					data-testid={`onboarding-docs-link-${step.key}`}
				>
					Read the step-by-step guide
					<ExternalLink class="h-3 w-3" />
				</a>
			</div>
		{/if}
	</div>

	<!-- Optional: snap-a-photo pre-fill (lease step). -->
	{#if photoPrefill}
		<div class="mb-5" data-testid={`onboarding-photo-prefill-${step.key}`}>
			{@render photoPrefill()}
		</div>
	{/if}

	<!-- The 1–2 field inputs. -->
	{@render children()}
</div>
