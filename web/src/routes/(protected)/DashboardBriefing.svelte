<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { ai, type BriefingBullet } from '$lib/api/endpoints/ai';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { recordHref, type RecordType } from '$lib/navigation/record-href';
	import {
		AlertTriangle,
		ChevronRight,
		CircleCheckBig,
		ListChecks,
		Sparkles
	} from '@lucide/svelte';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import AIBadge from '$lib/components/shared/AIBadge.svelte';

	const briefingQuery = createQuery(() => ({
		queryKey: ['ai-briefing', getCurrentPortfolioId()],
		queryFn: () => ai.briefing(),
		// The API already falls back to rules-only copy when optional AI wording is unavailable.
		// Do not turn one slow provider call into a minute of automatic retry skeletons.
		retry: false
	}));

	function recordTypeForEntity(entityType: string | null | undefined): RecordType | null {
		switch (entityType) {
			case 'WorkOrder':
				return 'workOrder';
			case 'Payment':
				return 'payment';
			case 'LeaseManagement':
				return 'leaseManagement';
			case 'Expense':
				return 'expense';
			case 'RentalApplication':
			case 'Application':
				return 'application';
			default:
				return null;
		}
	}

	function recordEntityHref(
		entityType: string | null | undefined,
		entityId: number | null | undefined,
		unitId?: number | null
	): string | null {
		if (!entityType || entityId == null) return null;
		const type = recordTypeForEntity(entityType);
		return type ? recordHref(type, { id: entityId, unitId }) : null;
	}

	function bulletHref(bullet: BriefingBullet): string | null {
		if (!bullet.entityType || bullet.entityId == null) return null;
		if (bullet.entityType === 'TenantAccount') {
			return bullet.unitId
				? `/units/${bullet.unitId}?tab=money&tenantAccount=${bullet.entityId}`
				: `/tenant-accounts/${bullet.entityId}`;
		}
		if (bullet.entityType === 'LeaseAgreement') {
			return bullet.unitId
				? `/units/${bullet.unitId}?tab=tenant-lease&view=agreements&agreement=${bullet.entityId}`
				: `/lease-agreements/${bullet.entityId}`;
		}
		const unitHref = recordEntityHref(bullet.entityType, bullet.entityId, bullet.unitId);
		if (unitHref) return unitHref;
		switch (bullet.entityType) {
			case 'Appointment':
				return `/appointments/${bullet.entityId}`;
			case 'Inspection':
				return `/maintenance/inspections/${bullet.entityId}`;
			default:
				return null;
		}
	}

	const severityStyles = {
		critical: {
			rail: 'bg-destructive',
			pill: 'border-destructive/40 bg-destructive/10 text-destructive'
		},
		warning: {
			rail: 'bg-warning',
			pill: 'border-warning/40 bg-warning/10 text-warning'
		},
		info: {
			rail: 'bg-primary/60',
			pill: 'border-border bg-muted text-muted-foreground'
		}
	} as const;

	function severityStyle(severity: string) {
		return severityStyles[severity as keyof typeof severityStyles] ?? severityStyles.info;
	}

	const ACTION_ITEM_LIMIT = 6;
</script>

<Card.Root
	class="m3-dashboard-art m3-dashboard-art--briefing m3-surface-art m3-surface-art--hero m3-art-04 m3-motion-enter relative mb-6 gap-0 overflow-hidden py-0"
	style="--m3-motion-index: 1"
	data-testid="dashboard-todays-briefing"
>
	<Card.Header class="relative px-5 pt-5 pb-3">
		<Card.Title class="flex items-center gap-2 text-lg font-semibold">
			<span
				class="flex h-7 w-7 items-center justify-center rounded-[var(--m3-shape-large)] bg-primary/15 ring-1 ring-primary/25"
			>
				<Sparkles class="h-4 w-4 text-primary" />
			</span>
			Today's Briefing
			<AIBadge />
		</Card.Title>
	</Card.Header>
	<Card.Content class="relative px-5 pb-5 pt-0">
		{#if briefingQuery.isLoading}
			<div class="grid min-w-0 gap-5 lg:grid-cols-5" data-testid="dashboard-briefing-loading">
				<div class="min-w-0 lg:col-span-3">
					<div class="h-28 w-full animate-pulse rounded-xl bg-muted"></div>
				</div>
				<div class="min-w-0 space-y-2 lg:col-span-2">
					{#each [0, 1, 2] as _}
						<div class="h-12 w-full animate-pulse rounded-lg bg-muted"></div>
					{/each}
				</div>
			</div>
		{:else}
			{@const briefing = briefingQuery.isError ? undefined : briefingQuery.data}
			{@const bullets = briefing?.bullets ?? []}
			{@const shown = bullets.slice(0, ACTION_ITEM_LIMIT)}
			{@const overflow = bullets.length - shown.length}
			<div class="grid min-w-0 gap-5 lg:grid-cols-5">
				<div class="min-w-0 lg:col-span-3">
					{#if briefing?.summary}
						<div
							class="flex flex-col gap-3 rounded-[var(--m3-shape-large)] bg-[color-mix(in_srgb,var(--primary)_12%,transparent)] p-4 ring-1 ring-primary/25"
							data-testid="dashboard-briefing-narrative"
						>
							<div class="flex items-center gap-2">
								<span
									class="flex h-6 w-6 items-center justify-center rounded-full bg-primary/20 ring-1 ring-primary/25"
								>
									<Sparkles class="h-3.5 w-3.5 text-primary" />
								</span>
								<span class="m3-type-label-medium text-primary">The assistant says</span>
							</div>
							<p class="break-words text-[15px] leading-relaxed text-foreground">
								{briefing.summary}
							</p>
						</div>
					{:else if briefingQuery.isError}
						<div
							class="rounded-xl bg-muted/40 p-4 text-sm text-muted-foreground"
							data-testid="dashboard-briefing-narrative"
						>
							<p>Your daily briefing took too long to load.</p>
							<Button
								variant="outline"
								size="sm"
								class="mt-3"
								onclick={() => briefingQuery.refetch()}
							>
								Try again
							</Button>
						</div>
					{:else}
						<div
							class="flex flex-col gap-2 rounded-[var(--m3-shape-large)] bg-[color-mix(in_srgb,var(--primary)_12%,transparent)] p-4 ring-1 ring-primary/25"
							data-testid="dashboard-briefing-narrative"
						>
							<div class="flex items-center gap-2">
								<span
									class="flex h-6 w-6 items-center justify-center rounded-full bg-primary/20 ring-1 ring-primary/25"
								>
									<Sparkles class="h-3.5 w-3.5 text-primary" />
								</span>
								<span class="m3-type-label-medium text-primary">The assistant says</span>
							</div>
							<p class="text-[15px] leading-relaxed text-foreground">
								{#if bullets.length > 0}
									You've got {bullets.length} thing{bullets.length === 1 ? '' : 's'} to look at
									today — they're listed to the right, most urgent first.
								{:else}
									Nothing urgent on your plate today. Everything's running smoothly across your
									portfolio.
								{/if}
							</p>
						</div>
					{/if}
				</div>

				<div class="min-w-0 lg:col-span-2" data-testid="dashboard-briefing-actions">
					<div class="mb-2 flex items-center gap-2">
						{#if bullets.length > 0}
							<ListChecks class="h-4 w-4 text-muted-foreground" />
							<h2 class="text-sm font-semibold text-foreground">
								{bullets.length} thing{bullets.length === 1 ? '' : 's'} need{bullets.length === 1
									? 's'
									: ''} attention
							</h2>
						{:else if briefingQuery.isError}
							<AlertTriangle class="h-4 w-4 text-muted-foreground" />
							<h2 class="text-sm font-semibold text-foreground">Action items unavailable</h2>
						{:else}
							<CircleCheckBig class="h-4 w-4 text-success" />
							<h2 class="text-sm font-semibold text-foreground">You're all caught up</h2>
						{/if}
					</div>
					{#if bullets.length === 0}
						<p class="text-sm text-muted-foreground">
							{briefingQuery.isError
								? "We couldn't load today's action items."
								: 'No priority items for today.'}
						</p>
					{:else}
						<div class="space-y-2">
							{#each shown as bullet}
								{@const sev = severityStyle(bullet.severity)}
								{@const href = bulletHref(bullet)}
								{#if href}
									<a
										{href}
										class="group relative flex w-full min-w-0 max-w-full items-start gap-2 overflow-hidden rounded-lg border border-border bg-background py-2 pl-4 pr-3 transition-colors hover:border-border/80 hover:bg-muted/40 sm:gap-3"
										data-testid="dashboard-briefing-action-item"
									>
										<span class="absolute inset-y-0 left-0 w-1 {sev.rail}"></span>
										<div class="min-w-0 flex-1">
											<div class="flex min-w-0 items-start justify-between gap-2">
												<p class="min-w-0 flex-1 break-words text-sm font-medium leading-snug text-foreground sm:truncate">
													{bullet.title}
												</p>
												<span
													class="shrink-0 rounded-full border px-2 py-0.5 text-[10px] font-medium capitalize {sev.pill}"
													>{bullet.severity}</span
												>
											</div>
											<p class="mt-0.5 line-clamp-2 text-xs text-muted-foreground">
												{bullet.detail}
											</p>
										</div>
										<ChevronRight
											class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground/60 transition-transform group-hover:translate-x-0.5 group-hover:text-muted-foreground"
										/>
									</a>
								{:else}
									<div
										class="relative flex w-full min-w-0 max-w-full items-start gap-2 overflow-hidden rounded-lg border border-border bg-background py-2 pl-4 pr-3 sm:gap-3"
										data-testid="dashboard-briefing-action-item"
									>
										<span class="absolute inset-y-0 left-0 w-1 {sev.rail}"></span>
										<div class="min-w-0 flex-1">
											<div class="flex min-w-0 items-start justify-between gap-2">
												<p class="min-w-0 flex-1 break-words text-sm font-medium leading-snug text-foreground sm:truncate">
													{bullet.title}
												</p>
												<span
													class="shrink-0 rounded-full border px-2 py-0.5 text-[10px] font-medium capitalize {sev.pill}"
													>{bullet.severity}</span
												>
											</div>
											<p class="mt-0.5 line-clamp-2 text-xs text-muted-foreground">
												{bullet.detail}
											</p>
										</div>
									</div>
								{/if}
							{/each}
							{#if overflow > 0}
								<p
									class="pt-0.5 text-xs text-muted-foreground"
									data-testid="dashboard-briefing-overflow"
								>
									+{overflow} more item{overflow === 1 ? '' : 's'}
								</p>
							{/if}
						</div>
					{/if}
				</div>
			</div>
		{/if}
	</Card.Content>
</Card.Root>
