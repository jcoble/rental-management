<script lang="ts">
	import { beforeNavigate } from '$app/navigation';
	import type { Snippet } from 'svelte';
	import { ArrowLeft, Check, LockKeyhole } from '@lucide/svelte';
	import NotificationHelpAction from './NotificationHelpAction.svelte';

	type JourneyStep = {
		number: 1 | 2 | 3;
		title: string;
		shortTitle: string;
		href: string;
	};

	let {
		currentStep,
		description,
		savedSummary,
		returnHref,
		canManage = true,
		hasUnsavedChanges = false,
		helpTitle,
		helpDescription,
		helpGuidance,
		helpHref = '/docs/settings-and-notifications',
		helpLinkLabel = 'Open notification documentation',
		children
	}: {
		currentStep: 1 | 2 | 3;
		description: string;
		savedSummary: string;
		returnHref: string;
		canManage?: boolean;
		hasUnsavedChanges?: boolean;
		helpTitle: string;
		helpDescription: string;
		helpGuidance: string;
		helpHref?: string;
		helpLinkLabel?: string;
		children: Snippet;
	} = $props();

	const steps: JourneyStep[] = [
		{
			number: 1,
			title: 'How should Rental Command reach you?',
			shortTitle: 'Your alerts',
			href: '/settings/notifications/my-alerts'
		},
		{
			number: 2,
			title: 'Who should handle each kind of work?',
			shortTitle: 'Team responsibilities',
			href: '/settings/notifications/team-routing'
		},
		{
			number: 3,
			title: 'Which tenant messages should Rental Command prepare?',
			shortTitle: 'Tenant messages',
			href: '/settings/notifications/tenant-notices'
		}
	];

	const activeStep = $derived(steps[currentStep - 1]);
	const previousStep = $derived(currentStep > 1 ? steps[currentStep - 2] : null);
	const nextStep = $derived(currentStep < 3 ? steps[currentStep] : null);

	beforeNavigate(({ cancel }) => {
		if (!hasUnsavedChanges) return;
		if (!window.confirm('You have unsaved notification changes. Leave this page without saving them?')) {
			cancel();
		}
	});
</script>

<div class="mx-auto max-w-5xl space-y-6" data-testid="notification-setup-journey">
	<header class="space-y-5">
		<a
			href={returnHref}
			class="inline-flex min-h-11 items-center gap-2 text-sm text-muted-foreground hover:text-foreground"
		>
			<ArrowLeft class="size-4" /> Back to settings
		</a>

		<div class="flex flex-wrap items-start justify-between gap-4">
			<div class="max-w-3xl">
				<p class="text-sm font-medium text-primary">Notification setup · Step {currentStep} of 3</p>
				<h1 class="mt-1 text-2xl font-semibold tracking-tight">{activeStep.title}</h1>
				<p class="mt-2 text-sm leading-6 text-muted-foreground">{description}</p>
			</div>
			<NotificationHelpAction
				title={helpTitle}
				description={helpDescription}
				guidance={helpGuidance}
				href={helpHref}
				linkLabel={helpLinkLabel}
			/>
		</div>

		<nav aria-label="Notification setup steps">
			<ol class="grid gap-2 sm:grid-cols-3">
				{#each steps as step (step.number)}
					<li>
						{#if step.number > 1 && !canManage && step.number !== currentStep}
							<span
								class="flex min-h-16 items-center gap-3 rounded-lg border border-border bg-muted/30 px-4 py-3 text-muted-foreground"
								aria-label={`${step.shortTitle}, administrator access required`}
							>
								<LockKeyhole class="size-4 shrink-0" />
								<span>
									<span class="block text-xs">Step {step.number}</span>
									<span class="block text-sm font-medium">{step.shortTitle}</span>
								</span>
							</span>
						{:else}
							<a
								href={step.href}
								aria-current={step.number === currentStep ? 'step' : undefined}
								class={`flex min-h-16 items-center gap-3 rounded-lg border px-4 py-3 transition-colors ${
									step.number === currentStep
										? 'border-primary bg-primary/5 text-foreground'
										: 'border-border text-muted-foreground hover:bg-muted/50 hover:text-foreground'
								}`}
							>
								<span
									class={`inline-flex size-7 shrink-0 items-center justify-center rounded-full text-xs font-semibold ${
										step.number < currentStep
											? 'bg-primary text-primary-foreground'
											: 'border border-current'
									}`}
								>
									{#if step.number < currentStep}
										<Check class="size-4" />
									{:else}
										{step.number}
									{/if}
								</span>
								<span class="text-sm font-medium">{step.shortTitle}</span>
							</a>
						{/if}
					</li>
				{/each}
			</ol>
		</nav>

		<div class="rounded-lg border border-border bg-muted/30 px-4 py-3" aria-live="polite">
			<p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Saved summary</p>
			<p class="mt-1 text-sm text-foreground">{savedSummary}</p>
		</div>
	</header>

	{@render children()}

	<footer class="flex flex-wrap items-center justify-between gap-3 border-t border-border pt-5">
		<div>
			{#if previousStep}
				<a
					href={previousStep.href}
					class="inline-flex min-h-11 items-center rounded-md border border-border px-4 text-sm font-medium hover:bg-muted"
				>
					Back: {previousStep.shortTitle}
				</a>
			{/if}
		</div>
		<div>
			{#if nextStep && (canManage || nextStep.number === 1)}
				<a
					href={nextStep.href}
					class="inline-flex min-h-11 items-center rounded-md bg-primary px-4 text-sm font-medium text-primary-foreground hover:bg-primary/90"
				>
					Next: {nextStep.shortTitle}
				</a>
			{:else if nextStep}
				<p class="max-w-sm text-right text-sm text-muted-foreground">
					An administrator manages the remaining notification steps.
				</p>
			{:else}
				<a
					href={returnHref}
					class="inline-flex min-h-11 items-center rounded-md bg-primary px-4 text-sm font-medium text-primary-foreground hover:bg-primary/90"
				>
					Finish notification setup
				</a>
			{/if}
		</div>
	</footer>
</div>
