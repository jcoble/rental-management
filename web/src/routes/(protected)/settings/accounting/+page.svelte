<script lang="ts">
	import { enhance } from '$app/forms';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import type { PageData, ActionData } from './$types';
	import type { ProviderView, AccountingMapping, AccountingReviewItem } from './+page.server';
	import { Button } from '$lib/components/ui/button';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import * as Card from '$lib/components/ui/card';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import RangeDatePicker from '$lib/components/shared/RangeDatePicker.svelte';
	import { showSuccess, showError } from '$lib/utils/toast';
	import { formatRelative } from '$lib/utils/date';
	import { accountingReviewCreateTarget } from '$lib/accounting/review-create-target';
	import {
		ArrowLeft,
		BadgeDollarSign,
		ArrowDownToLine,
		ArrowUpFromLine,
		Check,
		Link2,
		RefreshCw,
		TriangleAlert,
		Inbox,
		Lock
	} from '@lucide/svelte';

	let { data, form }: { data: PageData; form: ActionData } = $props();

	// Connection-status → StatusBadge tone. Connected reads as "live/good" (success); a token that
	// needs re-consent is a warning, not a hard error; Error is red; Pending/Disconnected stay calm.
	const STATUS_MAP = {
		Connected: { label: 'Connected', class: 'm3-tone-chip border m3-tone--success' },
		NeedsReconnect: { label: 'Reconnect needed', class: 'm3-tone-chip border m3-tone--warning' },
		Error: { label: 'Error', class: 'm3-tone-chip border m3-tone--error' },
		Pending: { label: 'Connecting…', class: 'm3-tone-chip border m3-tone--info' },
		Disconnected: { label: 'Disconnected', class: 'bg-muted text-muted-foreground border-border' }
	} as const;

	// Per-provider in-flight latches so each button shows progress on its own card.
	let connectingProvider = $state<string | null>(null);
	let importing = $state<string | null>(null);
	let confirmingMapping = $state<number | null>(null);
	let disconnecting = $state<string | null>(null);

	// Per-provider backfill date range (bound from RangeDatePicker), keyed by provider code.
	let importRange = $state<Record<string, { start: string; end: string }>>({});

	// Direction forms, keyed by provider, so a toggle can programmatically resubmit.
	let directionForms = $state<Record<string, HTMLFormElement | null>>({});

	// Toggling "pull" writes the desired pull/push into the hidden inputs of that provider's
	// direction form, then submits it (push stays off in v1). Pushed through the form so the same
	// `enhance` handler + toast applies whether the user clicks Save or flips the checkbox.
	function submitDirection(provider: string, pullEnabled: boolean) {
		const f = directionForms[provider];
		if (!f) return;
		const pull = f.querySelector<HTMLInputElement>('input[name="pullEnabled"]');
		if (pull) pull.value = pullEnabled ? 'true' : 'false';
		const operationKey = f.querySelector<HTMLInputElement>('input[name="operationKey"]');
		if (operationKey) {
			operationKey.value = pullEnabled
				? operationKey.dataset.enableKey ?? ''
				: operationKey.dataset.disableKey ?? '';
		}
		f.requestSubmit();
	}

	// One-time toast for the OAuth return (?connected=1 / ?error=…), then strip the param so a
	// refresh doesn't re-toast. No precedent for this in the app, so it's built here with $app/state.
	let handledReturn = $state(false);
	$effect(() => {
		if (handledReturn) return;
		const params = page.url.searchParams;
		const connected = params.get('connected');
		const error = params.get('error');
		if (!connected && !error) return;

		handledReturn = true;
		if (connected === '1') {
			showSuccess('Your accounting system is connected. Your books are syncing in.');
		} else if (error) {
			showError(returnErrorMessage(error));
		}
		// Drop the query param without a navigation/scroll jump.
		goto(page.url.pathname, { replaceState: true, noScroll: true, keepFocus: true, invalidateAll: false });
	});

	function returnErrorMessage(code: string): string {
		switch (code) {
			case 'missing_code_or_state':
				return "We didn't get a valid response back from your accounting provider. Please try connecting again.";
			case 'connect_failed':
				return 'We could not finish connecting your accounting system. Please try again.';
			case 'access_denied':
				return 'Connection cancelled — you did not grant access.';
			default:
				return `Could not connect: ${decodeURIComponent(code)}`;
		}
	}

	const connectedStatuses = ['Connected', 'NeedsReconnect'];
	const isConnected = (s: ProviderView) =>
		s.status.status != null && connectedStatuses.includes(s.status.status);

	// A friendlier "Account" / "Customer" → human noun for the mapping panel.
	function externalNoun(type: string): string {
		switch (type) {
			case 'Customer':
				return 'Customer';
			case 'Vendor':
				return 'Vendor';
			case 'Account':
				return 'Account';
			case 'Class':
				return 'Class';
			default:
				return type;
		}
	}

	function localNoun(type: string): string {
		switch (type) {
			case 'Tenant':
				return 'Tenant';
			case 'LeaseManagement':
			case 'LeaseAgreement':
				return 'Lease';
			case 'Vendor':
				return 'Vendor';
			case 'Property':
				return 'Property';
			case 'ScheduleECategory':
				return 'Schedule-E category';
			default:
				return type;
		}
	}

	// "system auto-link" vs "you confirmed it": the backend marks an auto-link with confirmedAt set
	// but no confirming user; a landlord confirm carries the user. We can't see the user id in the
	// DTO, so we treat any confirmed-with-confidence as an auto-link hint and a plain confirm as manual.
	function mappingProvenance(m: AccountingMapping): string {
		if (!m.confirmed) return 'Suggested';
		return m.confidence != null ? 'Auto-linked' : 'You confirmed';
	}

	function confidencePct(c: number | null): string {
		if (c == null) return '';
		return `${Math.round(c * 100)}% match`;
	}
</script>

<svelte:head>
	<title>Connect your accounting - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20">
	<div class="mb-4 max-w-2xl">
		<a
			href="/settings"
			class="mb-3 inline-flex items-center gap-1.5 text-sm font-medium text-muted-foreground hover:text-foreground"
			data-testid="accounting-back-to-settings"
		>
			<ArrowLeft class="h-4 w-4" /> Back to settings
		</a>
		<div class="flex items-center gap-2">
			<BadgeDollarSign class="h-5 w-5 text-primary" />
			<h1 class="text-2xl font-bold">Connect your accounting</h1>
		</div>
		<p class="text-sm text-muted-foreground">
			Link the books you already keep so your rent payments and expenses flow into Rental Command —
			no double entry. We show you exactly what's coming in and let you confirm each match.
		</p>
	</div>

	{#if data.loadError}
		<div
			class="max-w-2xl rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive"
			role="alert"
			data-testid="accounting-load-error"
		>
			{data.loadError}
		</div>
	{:else if data.providers.length === 0}
		<p class="max-w-2xl text-sm text-muted-foreground" data-testid="accounting-no-providers">
			No accounting providers are available yet.
		</p>
	{:else}
		<div class="max-w-2xl space-y-4" data-testid="accounting-provider-list">
			{#each data.providers as view (view.status.provider)}
				{@const s = view.status}
				<Card.Root class="gap-0 py-0" data-testid={`accounting-provider-${s.provider}`}>
					<Card.Header class="flex flex-row items-center justify-between gap-3 border-b border-border px-5 py-4">
						<div class="flex items-center gap-2.5">
							<span class="flex h-9 w-9 shrink-0 items-center justify-center rounded-md bg-primary/10 text-primary">
								<BadgeDollarSign class="h-5 w-5" />
							</span>
							<div>
								<p class="text-sm font-semibold leading-tight" data-testid={`accounting-provider-name-${s.provider}`}>
									{s.providerName}
								</p>
								{#if s.companyName}
									<p class="text-xs text-muted-foreground">{s.companyName}</p>
								{/if}
							</div>
						</div>

						{#if !s.configured}
							<span
								class="rounded-full bg-muted px-2.5 py-1 text-xs font-medium text-muted-foreground"
								data-testid={`accounting-status-${s.provider}`}
							>
								Not configured
							</span>
						{:else if s.status == null}
							<span
								class="rounded-full bg-muted px-2.5 py-1 text-xs font-medium text-muted-foreground"
								data-testid={`accounting-status-${s.provider}`}
							>
								Not connected
							</span>
						{:else}
							<span data-testid={`accounting-status-${s.provider}`}>
								<StatusBadge status={s.status} map={STATUS_MAP} />
							</span>
						{/if}
					</Card.Header>

					<Card.Content class="space-y-5 p-5">
						{#if !s.configured}
							<!-- AC-8: fail-closed when the box has no provider credentials. -->
							<div class="flex items-start gap-2.5 rounded-md border border-border bg-muted/40 p-3">
								<Lock class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
								<p class="text-sm text-muted-foreground">
									{s.providerName} isn't set up on this server yet. Ask your administrator to add the
									{s.providerName} keys, then come back to connect it.
								</p>
							</div>
						{:else if !isConnected(view)}
							<!-- Configured, but this portfolio hasn't connected (or has disconnected). -->
							<p class="text-sm text-muted-foreground">
								Connect {s.providerName} to bring your existing payments and expenses into Rental
								Command. You'll review and confirm how things match before anything is created.
							</p>
							<form
								method="POST"
								action="?/connect"
								use:enhance={() => {
									connectingProvider = s.provider;
									return async ({ result }) => {
										if (result.type === 'success' && result.data?.authorizeUrl) {
											// Full-page redirect to the provider's OAuth consent screen.
											window.location.href = result.data.authorizeUrl as string;
											return;
										}
										connectingProvider = null;
										if (result.type === 'failure') {
											showError((result.data?.error as string) ?? 'Could not start the connection.');
										}
									};
								}}
							>
								<input type="hidden" name="provider" value={s.provider} />
								<input type="hidden" name="operationKey" value={view.connectOperationId} />
								<Button
									type="submit"
									disabled={connectingProvider === s.provider}
									data-testid={`accounting-connect-${s.provider}`}
								>
									<Link2 class="mr-1.5 h-4 w-4" />
									{connectingProvider === s.provider ? 'Connecting…' : `Connect ${s.providerName}`}
								</Button>
							</form>
						{:else}
							<!-- Connected (or needs reconnect). -->
							{#if s.status === 'NeedsReconnect'}
								<div
									class="flex items-start gap-2.5 rounded-md border border-amber-500/30 bg-amber-500/10 p-3"
									role="alert"
									data-testid={`accounting-reconnect-notice-${s.provider}`}
								>
									<TriangleAlert class="mt-0.5 h-4 w-4 shrink-0 text-amber-600 dark:text-amber-400" />
									<div class="space-y-2">
										<p class="text-sm text-amber-700 dark:text-amber-300">
											{s.providerName} needs you to reconnect — its access expired, so syncing is paused.
											{#if s.lastError}<span class="block text-xs opacity-80">{s.lastError}</span>{/if}
										</p>
										<form
											method="POST"
											action="?/connect"
											use:enhance={() => {
												connectingProvider = s.provider;
												return async ({ result }) => {
													if (result.type === 'success' && result.data?.authorizeUrl) {
														window.location.href = result.data.authorizeUrl as string;
														return;
													}
													connectingProvider = null;
													if (result.type === 'failure') {
														showError((result.data?.error as string) ?? 'Could not start the reconnection.');
													}
												};
											}}
										>
											<input type="hidden" name="provider" value={s.provider} />
											<input type="hidden" name="operationKey" value={view.connectOperationId} />
											<Button
												type="submit"
												size="sm"
												disabled={connectingProvider === s.provider}
												data-testid={`accounting-reconnect-${s.provider}`}
											>
												<RefreshCw class="mr-1.5 h-4 w-4" />
												{connectingProvider === s.provider ? 'Reconnecting…' : 'Reconnect'}
											</Button>
										</form>
									</div>
								</div>
							{/if}

							<!-- Last sync + counts -->
							<div class="grid grid-cols-3 gap-3 text-center">
								<div class="rounded-md border border-border p-3">
									<p class="text-lg font-semibold tabular-nums" data-testid={`accounting-imported-count-${s.provider}`}>
										{s.importedCount}
									</p>
									<p class="text-xs text-muted-foreground">Imported</p>
								</div>
								<div class="rounded-md border border-border p-3">
									<p class="text-lg font-semibold tabular-nums" data-testid={`accounting-pending-count-${s.provider}`}>
										{s.pendingReviewCount}
									</p>
									<p class="text-xs text-muted-foreground">Need review</p>
								</div>
								<div class="rounded-md border border-border p-3">
									<p class="text-sm font-medium" data-testid={`accounting-last-sync-${s.provider}`}>
										{s.lastSyncedAt ? formatRelative(s.lastSyncedAt) : 'Never'}
									</p>
									<p class="text-xs text-muted-foreground">Last synced</p>
								</div>
							</div>

							<!-- Direction chooser -->
							<div class="space-y-2">
								<p class="text-sm font-semibold">What flows, and which way</p>
								<form
									method="POST"
									action="?/setDirection"
									bind:this={directionForms[s.provider]}
									use:enhance={() => {
										return async ({ result, update }) => {
											if (result.type === 'success') {
												showSuccess('Saved your sync direction.');
												await update({ reset: false });
											} else if (result.type === 'failure') {
												showError((result.data?.error as string) ?? 'Could not save the direction.');
											}
										};
									}}
								>
									<input type="hidden" name="provider" value={s.provider} />
									<input
										type="hidden"
										name="operationKey"
										value={s.pullEnabled ? view.pullDisableOperationId : view.pullEnableOperationId}
										data-enable-key={view.pullEnableOperationId}
										data-disable-key={view.pullDisableOperationId}
									/>
									<!-- Desired direction is carried in hidden inputs the toggle rewrites before submit.
									     Push stays off in v1 (its UI control is disabled below). -->
									<input type="hidden" name="pullEnabled" value={s.pullEnabled ? 'true' : 'false'} />
									<input type="hidden" name="pushEnabled" value="false" />

									<!-- Pull: the primary direction. Flipping it submits the form. -->
									<label
										class="flex items-start gap-3 rounded-md border border-border p-3"
										data-testid={`accounting-pull-row-${s.provider}`}
									>
										<Checkbox
											checked={s.pullEnabled}
											data-testid={`accounting-pull-toggle-${s.provider}`}
											onCheckedChange={(v) => submitDirection(s.provider, v === true)}
										/>
										<span class="text-sm leading-tight">
											<span class="flex items-center gap-1.5 font-medium">
												<ArrowDownToLine class="h-3.5 w-3.5 text-primary" /> Bring my books into Rental
												Command
											</span>
											<span class="block text-xs text-muted-foreground">
												Pull your customers, vendors, payments and expenses in and match them to your
												leases, properties and vendors. This is the main reason to connect.
											</span>
										</span>
									</label>

									<!-- Push: v1.1. Present but disabled with a "coming soon" hint. -->
									<label
										class="mt-2 flex cursor-not-allowed items-start gap-3 rounded-md border border-dashed border-border p-3 opacity-70"
										data-testid={`accounting-push-row-${s.provider}`}
									>
										<Checkbox
											checked={s.pushEnabled}
											disabled
											data-testid={`accounting-push-toggle-${s.provider}`}
										/>
										<span class="text-sm leading-tight">
											<span class="flex items-center gap-1.5 font-medium">
												<ArrowUpFromLine class="h-3.5 w-3.5" /> Send Rental Command into my books
												<span class="rounded-full bg-muted px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">
													Coming soon
												</span>
											</span>
											<span class="block text-xs text-muted-foreground">
												Push payments and expenses you record here back out to {s.providerName}. We're
												finishing this — it'll arrive in a later update.
											</span>
										</span>
									</label>
								</form>
							</div>

							<!-- Backfill import -->
							<div class="space-y-2">
								<p class="text-sm font-semibold">Bring in past activity</p>
								<p class="text-xs text-muted-foreground">
									Pick a date range to pull older payments and expenses. We never create the same one
									twice, so it's safe to run again.
								</p>
								<form
									method="POST"
									action="?/import"
									use:enhance={() => {
										importing = s.provider;
										return async ({ result, update }) => {
											importing = null;
											if (result.type === 'success' && result.data?.imported) {
												const r = result.data.imported as {
													paymentsImported: number;
													expensesImported: number;
													needsReview: number;
												};
												showSuccess(
													`Imported ${r.paymentsImported} payment(s) and ${r.expensesImported} expense(s).` +
														(r.needsReview ? ` ${r.needsReview} need your review.` : '')
												);
												await update({ reset: false });
											} else if (result.type === 'failure') {
												showError((result.data?.error as string) ?? 'Import failed.');
											}
										};
									}}
									class="flex flex-wrap items-center gap-2"
								>
									<input type="hidden" name="provider" value={s.provider} />
									<input type="hidden" name="fromDate" value={importRange[s.provider]?.start ?? ''} />
									<input type="hidden" name="toDate" value={importRange[s.provider]?.end ?? ''} />
									<RangeDatePicker
										start={importRange[s.provider]?.start ?? ''}
										end={importRange[s.provider]?.end ?? ''}
										presets
										testid={`accounting-import-range-${s.provider}`}
										onchange={(r) => (importRange[s.provider] = r)}
									/>
									<Button
										type="submit"
										variant="outline"
										size="sm"
										disabled={importing === s.provider}
										data-testid={`accounting-import-${s.provider}`}
									>
										<ArrowDownToLine class="mr-1.5 h-4 w-4" />
										{importing === s.provider ? 'Importing…' : 'Import'}
									</Button>
								</form>
							</div>

							<!-- Mapping review -->
							{#if view.loadError}
								<p class="text-xs text-muted-foreground">Couldn't load matches: {view.loadError}</p>
							{/if}
							{#if view.unconfirmedMappings.length > 0}
								<div class="space-y-2" data-testid={`accounting-mappings-${s.provider}`}>
									<p class="text-sm font-semibold">Confirm what matches what</p>
									<p class="text-xs text-muted-foreground">
										We found these likely matches from your books. Confirm the ones that look right —
										nothing links until you say so.
									</p>
									{#if view.unconfirmedMappingsHasMore}
										<p class="text-xs text-muted-foreground">Showing the 50 most recent suggested matches.</p>
									{/if}
									<div class="space-y-2">
										{#each view.unconfirmedMappings as m (m.id)}
											<div
												class="flex flex-col gap-2 rounded-md border border-border p-3 sm:flex-row sm:items-center sm:justify-between"
												data-testid={`accounting-mapping-${m.id}`}
											>
												<div class="min-w-0 text-sm">
													<span class="font-medium">{m.externalDisplayName ?? m.externalId}</span>
													<span class="text-xs text-muted-foreground">
														({externalNoun(m.externalType)} from your books)
													</span>
													<span class="mx-1 text-muted-foreground">→</span>
													<span class="text-muted-foreground">{localNoun(m.localEntityType)}</span>
													{#if m.confidence != null}
														<span
															class="ml-1.5 rounded-full border border-border px-1.5 py-0.5 text-[10px] font-medium text-muted-foreground"
														>
															{confidencePct(m.confidence)}
														</span>
													{/if}
												</div>
												<form
													method="POST"
													action="?/confirmMapping"
													use:enhance={() => {
														confirmingMapping = m.id;
														return async ({ result, update }) => {
															confirmingMapping = null;
															if (result.type === 'success') {
																const promoted = (result.data?.promoted as number) ?? 0;
																showSuccess(
																	promoted > 0
																		? `Match confirmed — ${promoted} transaction(s) imported.`
																		: 'Match confirmed.'
																);
																await update({ reset: false });
															} else if (result.type === 'failure') {
																showError((result.data?.error as string) ?? 'Could not confirm the match.');
															}
														};
													}}
												>
													<input type="hidden" name="provider" value={s.provider} />
													<input type="hidden" name="externalType" value={m.externalType} />
													<input type="hidden" name="externalId" value={m.externalId} />
													<input type="hidden" name="externalDisplayName" value={m.externalDisplayName ?? ''} />
													<input type="hidden" name="localEntityType" value={m.localEntityType} />
													{#if m.localEntityId != null}
														<input type="hidden" name="localEntityId" value={m.localEntityId} />
													{/if}
													{#if m.localEnumValue}
														<input type="hidden" name="localEnumValue" value={m.localEnumValue} />
													{/if}
													<Button
														type="submit"
														size="sm"
														variant="outline"
														disabled={confirmingMapping === m.id}
														data-testid={`accounting-confirm-mapping-${m.id}`}
													>
														<Check class="mr-1.5 h-4 w-4" />
														{confirmingMapping === m.id ? 'Confirming…' : 'Confirm'}
													</Button>
												</form>
											</div>
										{/each}
									</div>
								</div>
							{/if}

							{#if view.confirmedMappings.length > 0}
								<details class="rounded-md border border-border" data-testid={`accounting-confirmed-mappings-${s.provider}`}>
									<summary class="cursor-pointer px-3 py-2 text-sm font-medium">
										Confirmed matches ({view.confirmedMappings.length}{view.confirmedMappingsHasMore ? '+' : ''})
									</summary>
									<div class="space-y-1.5 border-t border-border p-3">
										{#each view.confirmedMappings as m (m.id)}
											<div class="flex items-center justify-between gap-2 text-sm">
												<span class="min-w-0 truncate">
													<span class="font-medium">{m.externalDisplayName ?? m.externalId}</span>
													<span class="mx-1 text-muted-foreground">→</span>
													<span class="text-muted-foreground">{localNoun(m.localEntityType)}</span>
												</span>
												<span class="shrink-0 text-xs text-muted-foreground">{mappingProvenance(m)}</span>
											</div>
										{/each}
									</div>
								</details>
							{/if}

							<!-- Review queue -->
							{#if view.reviewQueue.length > 0}
								<div class="space-y-2" data-testid={`accounting-review-queue-${s.provider}`}>
									<p class="flex items-center gap-1.5 text-sm font-semibold">
										<Inbox class="h-4 w-4 text-muted-foreground" /> Needs your attention
									</p>
									<p class="text-xs text-muted-foreground">
										These came in but we couldn't tell where they belong. Confirm a match above, or
										add the missing tenant or vendor where that applies — none of these were dropped.
									</p>
									{#if view.reviewQueueHasMore}
										<p class="text-xs text-muted-foreground">Showing the 50 most recent items that need review.</p>
									{/if}
									<div class="space-y-2">
										{#each view.reviewQueue as item (item.id)}
											{@const createTarget = accountingReviewCreateTarget(item)}
											<div
												class="flex flex-col gap-1 rounded-md border border-border bg-muted/30 p-3 sm:flex-row sm:items-center sm:justify-between"
												data-testid={`accounting-review-item-${item.id}`}
											>
												<div class="min-w-0 text-sm">
													<span class="font-medium">{item.externalType} {item.externalId}</span>
													{#if item.reason}
														<span class="block text-xs text-muted-foreground">{item.reason}</span>
													{/if}
												</div>
												<div class="flex items-center gap-2">
													<StatusBadge
														status={item.status}
														map={{
															NeedsReview: { label: 'Needs review', class: 'm3-tone-chip border m3-tone--warning' },
															Unmatched: { label: 'Unmatched', class: 'bg-muted text-muted-foreground border-border' }
														}}
													/>
													{#if createTarget}
														<a
															href={createTarget.href}
															class="text-xs font-medium text-primary hover:underline"
															data-testid={`accounting-review-create-${item.id}`}
														>
															{createTarget.label}
														</a>
													{/if}
												</div>
											</div>
										{/each}
									</div>
								</div>
							{/if}

							<!-- Disconnect -->
							<div class="border-t border-border pt-4">
								<form
									method="POST"
									action="?/disconnect"
									use:enhance={() => {
										disconnecting = s.provider;
										return async ({ result, update }) => {
											disconnecting = null;
											if (result.type === 'success') {
												showSuccess(`Disconnected ${s.providerName}.`);
												await update({ reset: false });
											} else if (result.type === 'failure') {
												showError((result.data?.error as string) ?? 'Could not disconnect.');
											}
										};
									}}
								>
									<input type="hidden" name="provider" value={s.provider} />
									<input type="hidden" name="operationKey" value={view.disconnectOperationId} />
									<Button
										type="submit"
										variant="ghost"
										size="sm"
										class="text-destructive hover:text-destructive"
										disabled={disconnecting === s.provider}
										data-testid={`accounting-disconnect-${s.provider}`}
									>
										{disconnecting === s.provider ? 'Disconnecting…' : 'Disconnect'}
									</Button>
								</form>
							</div>
						{/if}
					</Card.Content>
				</Card.Root>
			{/each}
		</div>
	{/if}
</div>
