<script lang="ts">
	import { Check } from '@lucide/svelte';
	import type { Snippet } from 'svelte';

	export type FormStepperStep = {
		id: string;
		label: string;
		description?: string;
	};

	let {
		steps,
		currentStep = $bindable(0),
		completedSteps = [],
		testid = 'form-stepper',
		children
	}: {
		steps: FormStepperStep[];
		currentStep?: number;
		completedSteps?: number[];
		testid?: string;
		children: Snippet;
	} = $props();

	function canSelect(index: number) {
		return index <= currentStep || completedSteps.includes(index);
	}
</script>

<div class="space-y-5" data-testid={testid}>
	<div class="form-stepper-grid grid gap-2" style={`--step-count: ${steps.length}`}>
		{#each steps as step, index (step.id)}
			<button
				type="button"
				class="group min-w-0 rounded-lg border px-3 py-2 text-left transition duration-200 {index === currentStep
					? 'border-primary/60 bg-primary/10 text-foreground shadow-sm'
					: completedSteps.includes(index)
						? 'animate-step-complete border-success/40 bg-success/10 text-foreground'
						: 'border-border bg-muted/20 text-muted-foreground'}"
				disabled={!canSelect(index)}
				aria-current={index === currentStep ? 'step' : undefined}
				data-testid={`${testid}-step-${step.id}`}
				onclick={() => {
					if (canSelect(index)) currentStep = index;
				}}
			>
				<span class="flex items-center gap-2">
					<span
						class="grid h-6 w-6 shrink-0 place-items-center rounded-full border text-xs font-semibold transition duration-200 {completedSteps.includes(index)
							? 'scale-105 border-success bg-success text-success-foreground'
							: index === currentStep
								? 'border-primary bg-primary text-primary-foreground'
								: 'border-border bg-background text-muted-foreground'}"
						aria-hidden="true"
					>
						{#if completedSteps.includes(index)}
							<Check class="h-3.5 w-3.5" />
						{:else}
							{index + 1}
						{/if}
					</span>
					<span class="min-w-0">
						<span class="block truncate text-sm font-medium">{step.label}</span>
						{#if step.description}
							<span class="block truncate text-xs text-muted-foreground">{step.description}</span>
						{/if}
					</span>
				</span>
			</button>
		{/each}
	</div>

	<div class="animate-step-panel transition duration-200" data-testid={`${testid}-panel`}>
		{@render children()}
	</div>
</div>

<style>
	.form-stepper-grid {
		grid-template-columns: repeat(auto-fit, minmax(min(12rem, 100%), 1fr));
	}
	@keyframes step-complete-pop {
		0% {
			transform: scale(1);
		}
		45% {
			transform: scale(1.025);
		}
		100% {
			transform: scale(1);
		}
	}

	@keyframes step-panel-in {
		0% {
			opacity: 0;
			transform: translateY(4px);
		}
		100% {
			opacity: 1;
			transform: translateY(0);
		}
	}

	.animate-step-complete {
		animation: step-complete-pop 220ms ease-out;
	}

	.animate-step-panel {
		animation: step-panel-in 180ms ease-out;
	}
</style>
