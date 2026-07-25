<script lang="ts">
	import { page } from '$app/state';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { technician, type TechnicianWorkEntryKind, type TechnicianWorkOrderStatus } from '$lib/api/endpoints/technician';
	import { CAPABILITY } from '$lib/auth/experience-policy';
	import { documentFileHref, documents } from '$lib/api/endpoints/documents';
	import { workOrderStatusActionTargets } from '$lib/maintenance/work-order-dispatch';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { capabilityKeysForExperience } from '$lib/types/user';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { formatStatusLabel } from '$lib/utils/status-labels';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { ArrowLeft, CalendarClock, Camera, CheckCircle2, Clock3, History, Mail, MapPin, MessageSquare, Package, Phone, Send, UserRound } from '@lucide/svelte';

	const id = $derived(Number(page.params.id));
	const queryClient = useQueryClient();
	const authState = getAuthState();
	const currentAccess = $derived(page.data.access ?? authState.accessEnvelope);
	const currentExperience = $derived(
		authState.activeExperience ?? currentAccess?.selectedContext.activeExperience ?? null
	);
	const activeCapabilities = $derived(
		capabilityKeysForExperience(currentAccess, currentExperience)
	);
	const canUpdateWork = $derived(activeCapabilities.has(CAPABILITY.assignedWorkUpdate));
	const canManageTimeMaterials = $derived(
		activeCapabilities.has(CAPABILITY.assignedWorkTimeMaterialsManage)
	);
	const canConverse = $derived(activeCapabilities.has(CAPABILITY.assignedWorkConverse));
	const allowedEntryKinds = $derived<TechnicianWorkEntryKind[]>([
		...(canUpdateWork ? (['Note', 'Photo'] as TechnicianWorkEntryKind[]) : []),
		...(canManageTimeMaterials ? (['Time', 'Material'] as TechnicianWorkEntryKind[]) : [])
	]);
	const assignment = createQuery(() => ({
		queryKey: ['technician', 'assignment', id],
		queryFn: () => technician.detail(id),
		enabled: Number.isInteger(id) && id > 0
	}));
	const progressTargets = $derived<TechnicianWorkOrderStatus[]>(
		assignment.data
			? workOrderStatusActionTargets(assignment.data.status, true) as TechnicianWorkOrderStatus[]
			: []
	);

	let entryKind = $state<TechnicianWorkEntryKind>('Note');
	let entryNote = $state('');
	let quantity = $state('');
	let unit = $state('hours');
	let message = $state('');
	let photoFile = $state<File | null>(null);
	let nextStatus = $state<TechnicianWorkOrderStatus | ''>('');
	let progressNote = $state('');
	let markedConversationRead = $state(false);

	$effect(() => {
		if (!allowedEntryKinds.includes(entryKind) && allowedEntryKinds[0]) {
			entryKind = allowedEntryKinds[0];
		}
	});

	$effect(() => {
		if (nextStatus && !progressTargets.includes(nextStatus)) nextStatus = '';
	});

	$effect(() => {
		if (canConverse && !markedConversationRead && assignment.data?.conversationId) {
			markedConversationRead = true;
			technician.markConversationRead(id).then(refresh).catch(() => {});
		}
	});

	function refresh() {
		queryClient.invalidateQueries({ queryKey: ['technician'] });
	}

	const entryMutation = createMutation(() => ({
		mutationFn: async () => {
			if (!allowedEntryKinds.includes(entryKind)) {
				throw new Error('Your assignment does not allow this kind of field entry.');
			}
			if (entryKind === 'Photo') {
				if (!photoFile) throw new Error('Choose a photo first.');
				const uploaded = await documents.upload('WorkOrder', id, photoFile, 'Technician photo', crypto.randomUUID());
				return technician.recordEntry(id, { kind: 'Photo', note: entryNote.trim() || undefined, photoFileId: uploaded.id });
			}
			return technician.recordEntry(id, {
				kind: entryKind,
				note: entryNote.trim() || undefined,
				quantity: entryKind === 'Time' || entryKind === 'Material' ? Number(quantity) : undefined,
				unit: entryKind === 'Time' || entryKind === 'Material' ? unit.trim() : undefined
			});
		},
		onSuccess: () => {
			showSuccess(entryKind === 'Photo' ? 'Photo added.' : 'Work entry added.');
			entryNote = ''; quantity = ''; photoFile = null; refresh();
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));

	const messageMutation = createMutation(() => ({
		mutationFn: () => {
			if (!canConverse) throw new Error('Your assignment does not allow messaging.');
			return technician.sendMessage(id, message.trim());
		},
		onSuccess: () => { showSuccess('Message sent.'); message = ''; refresh(); },
		onError: (error) => showError(apiErrorMessage(error))
	}));

	const progressMutation = createMutation(() => ({
		mutationFn: () => {
			const work = assignment.data;
			if (!work) throw new Error('Refresh the assignment before updating it.');
			const note = progressNote.trim();
			if (!nextStatus && !note) throw new Error('Choose a status or add a progress note.');
			return technician.update(work.id, {
				expectedUpdatedAtUtc: work.updatedAtUtc,
				status: nextStatus || undefined,
				technicianNote: note || undefined
			});
		},
		onSuccess: () => {
			showSuccess(nextStatus ? `Assignment marked ${formatStatusLabel(nextStatus).toLowerCase()}.` : 'Progress note added.');
			nextStatus = '';
			progressNote = '';
			refresh();
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));

	function when(value?: string | null): string {
		return value ? new Date(value).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' }) : 'Not set';
	}
</script>

<svelte:head><title>{assignment.data?.title ?? 'Assignment'} - Rental Command</title></svelte:head>

<div class="mx-auto w-full max-w-6xl space-y-5 p-4 sm:p-6">
	<a href="/my-work" class="inline-flex items-center gap-2 text-sm text-muted-foreground hover:text-foreground"><ArrowLeft class="h-4 w-4" />My work</a>

	{#if assignment.isPending}
		<LoadingState label="Loading assignment" variant="page" testid="technician-assignment-loading" />
	{:else if assignment.isError || !assignment.data}
		<div class="rounded-2xl border border-destructive/40 bg-destructive/5 p-6">
			<p class="text-sm font-medium text-destructive">This assignment is unavailable or is no longer assigned to you.</p>
			{#if assignment.isError}
				<Button type="button" variant="outline" size="sm" class="mt-3" onclick={() => assignment.refetch()}>Try again</Button>
			{/if}
		</div>
	{:else}
		{@const work = assignment.data}
		<header class="rounded-3xl border border-border bg-card p-5 sm:p-6">
			<div class="flex flex-wrap items-start justify-between gap-3">
				<div><p class="text-sm text-muted-foreground">{work.category}</p><h1 class="mt-1 text-2xl font-semibold">{work.title}</h1></div>
				<StatusBadge status={work.status} />
			</div>
			<p class="mt-4 whitespace-pre-wrap text-sm leading-6">{work.description}</p>
		</header>

		<div class="grid gap-5 lg:grid-cols-[minmax(0,1.25fr)_minmax(20rem,.75fr)]">
			<div class="space-y-5">
				<section class="rounded-2xl border border-border bg-card p-5">
					<h2 class="font-semibold">Visit details</h2>
					<div class="mt-4 space-y-3 text-sm">
						<p class="flex gap-2"><MapPin class="mt-0.5 h-4 w-4 shrink-0 text-primary" /><span>{work.address}{work.unit ? ` · Unit ${work.unit}` : ''}</span></p>
						<p class="flex gap-2"><CalendarClock class="mt-0.5 h-4 w-4 shrink-0 text-primary" /><span>{when(work.scheduledForUtc)}{work.scheduledWindowEndUtc ? ` – ${when(work.scheduledWindowEndUtc)}` : ''}</span></p>
					</div>
					{#if work.accessInstructions}<div class="mt-4 rounded-xl bg-secondary/60 p-4"><p class="text-xs font-semibold uppercase tracking-wide text-muted-foreground">Access instructions</p><p class="mt-2 whitespace-pre-wrap text-sm">{work.accessInstructions}</p></div>{/if}
				</section>

				<section class="rounded-2xl border border-border bg-card p-5">
					<h2 class="font-semibold">Permitted contact</h2>
					{#if work.contactName}
						<div class="mt-4 space-y-2 text-sm">
							<p class="flex items-center gap-2"><UserRound class="h-4 w-4" />{work.contactName}</p>
							{#if work.contactPhone}<a class="flex items-center gap-2 text-primary" href="tel:{work.contactPhone}"><Phone class="h-4 w-4" />{work.contactPhone}</a>{/if}
							{#if work.contactEmail}<a class="flex items-center gap-2 text-primary" href="mailto:{work.contactEmail}"><Mail class="h-4 w-4" />{work.contactEmail}</a>{/if}
						</div>
					{:else}<p class="mt-3 text-sm text-muted-foreground">No resident contact is shared for this assignment.</p>{/if}
				</section>

				<section class="rounded-2xl border border-border bg-card p-5" data-testid="assignment-progress-update">
					<div class="flex flex-wrap items-start justify-between gap-3">
						<div>
							<h2 class="flex items-center gap-2 font-semibold"><CheckCircle2 class="h-4 w-4 text-primary" />Update progress</h2>
							<p class="mt-1 text-sm text-muted-foreground">Keep the office and resident informed without leaving this assignment.</p>
						</div>
						<StatusBadge status={work.status} />
					</div>

					{#if canUpdateWork}
						<div class="mt-4 grid gap-3 sm:grid-cols-[minmax(12rem,.65fr)_minmax(0,1fr)_auto] sm:items-end">
							<label class="grid gap-1.5 text-sm font-medium">
								<span>New status</span>
								<select bind:value={nextStatus} class="h-10 rounded-md border border-border bg-background px-3 text-sm" data-testid="assignment-progress-status">
									<option value="">Keep {formatStatusLabel(work.status)}</option>
									{#each progressTargets as status}<option value={status}>{formatStatusLabel(status)}</option>{/each}
								</select>
							</label>
							<label class="grid gap-1.5 text-sm font-medium">
								<span>Progress note <span class="font-normal text-muted-foreground">(optional with a status)</span></span>
								<Input bind:value={progressNote} placeholder="What changed or what happens next?" data-testid="assignment-progress-note" />
							</label>
							<Button
								disabled={progressMutation.isPending || (!nextStatus && !progressNote.trim())}
								onclick={() => progressMutation.mutate()}
								data-testid="assignment-progress-submit"
							>{progressMutation.isPending ? 'Saving…' : 'Save update'}</Button>
						</div>
						{#if progressTargets.length === 0}
							<p class="mt-3 text-xs text-muted-foreground">This assignment is closed for status changes. You can still add a final progress note.</p>
						{/if}
					{:else}
						<p class="mt-3 text-sm text-muted-foreground">You can review progress, but this assignment does not include permission to change it.</p>
					{/if}

					<div class="mt-5 border-t border-border pt-4">
						<h3 class="flex items-center gap-2 text-sm font-semibold"><History class="h-4 w-4" />Progress history</h3>
						<div class="mt-3 space-y-3">
							{#each work.timeline as item (item.id)}
								<div class="rounded-xl bg-secondary/45 p-3 text-sm" data-testid="assignment-progress-event-{item.id}">
									<div class="flex flex-wrap items-center justify-between gap-2">
										<p class="font-medium">{item.fromStatus ? `${formatStatusLabel(item.fromStatus)} → ` : ''}{formatStatusLabel(item.toStatus)}</p>
										<time class="text-xs text-muted-foreground">{when(item.createdAtUtc)}</time>
									</div>
									{#if item.note}<p class="mt-1 whitespace-pre-wrap text-muted-foreground">{item.note}</p>{/if}
									{#if item.changedBy}<p class="mt-1 text-xs text-muted-foreground">{item.changedBy}</p>{/if}
								</div>
							{:else}<p class="text-sm text-muted-foreground">No progress updates yet.</p>{/each}
						</div>
					</div>
				</section>

				<section class="rounded-2xl border border-border bg-card p-5">
					<h2 class="font-semibold">Field log</h2>
					{#if allowedEntryKinds.length > 0}<div class="mt-4 grid gap-3 sm:grid-cols-[10rem_1fr_auto]">
						<select bind:value={entryKind} class="h-10 rounded-md border border-border bg-background px-3 text-sm" aria-label="Entry type">
							{#each allowedEntryKinds as kind}<option value={kind}>{kind}</option>{/each}
						</select>
						{#if entryKind === 'Time' || entryKind === 'Material'}
							<div class="grid grid-cols-2 gap-2"><Input bind:value={quantity} type="number" min="0.001" step="0.25" placeholder="Quantity" /><Input bind:value={unit} placeholder={entryKind === 'Time' ? 'hours' : 'units'} /></div>
						{:else if entryKind === 'Photo'}
							<input type="file" accept="image/*" capture="environment" class="text-sm" onchange={(event) => photoFile = event.currentTarget.files?.[0] ?? null} />
						{:else}<Input bind:value={entryNote} placeholder="What did you find or do?" />{/if}
						<Button disabled={entryMutation.isPending || (entryKind === 'Note' && !entryNote.trim()) || ((entryKind === 'Time' || entryKind === 'Material') && (!quantity || !unit.trim())) || (entryKind === 'Photo' && !photoFile)} onclick={() => entryMutation.mutate()}>Add</Button>
					</div>{:else}<p class="mt-3 text-sm text-muted-foreground">You can review the field log, but this assignment does not allow you to add entries.</p>{/if}
					<div class="mt-5 space-y-3">
						{#each work.entries as entry (entry.id)}
							<div class="rounded-xl border border-border p-3 text-sm">
								<div class="flex items-center justify-between gap-2"><span class="flex items-center gap-2 font-medium">{#if entry.kind === 'Photo'}<Camera class="h-4 w-4" />{:else if entry.kind === 'Time'}<Clock3 class="h-4 w-4" />{:else if entry.kind === 'Material'}<Package class="h-4 w-4" />{/if}{entry.kind}</span><time class="text-xs text-muted-foreground">{when(entry.occurredAtUtc)}</time></div>
								{#if entry.note}<p class="mt-2 whitespace-pre-wrap">{entry.note}</p>{/if}
								{#if entry.quantity}<p class="mt-2">{entry.quantity} {entry.unit}</p>{/if}
								{#if entry.photoFileId}<img class="mt-3 max-h-72 rounded-lg object-contain" src={documentFileHref(entry.photoFileId, { thumb: true })} alt="Technician work" />{/if}
							</div>
						{:else}<p class="text-sm text-muted-foreground">No field entries yet.</p>{/each}
					</div>
				</section>
			</div>

			{#if canConverse}<section id="conversation" class="h-fit rounded-2xl border border-border bg-card p-5 lg:sticky lg:top-20">
				<h2 class="flex items-center gap-2 font-semibold"><MessageSquare class="h-4 w-4" />Assignment conversation</h2>
				<p class="mt-1 text-xs text-muted-foreground">Messages stay attached to this assignment.</p>
				<div class="mt-4 max-h-[28rem] space-y-3 overflow-y-auto">
					{#each work.messages as item (item.id)}
						<div class="rounded-xl p-3 text-sm {item.sender === 'You' ? 'ml-8 bg-primary text-primary-foreground' : 'mr-8 bg-secondary'}"><p class="text-xs font-semibold opacity-70">{item.sender}</p><p class="mt-1 whitespace-pre-wrap">{item.body}</p><time class="mt-2 block text-[11px] opacity-60">{when(item.createdAtUtc)}</time></div>
					{:else}<p class="py-8 text-center text-sm text-muted-foreground">No messages yet.</p>{/each}
				</div>
				<div class="mt-4 flex gap-2"><Input bind:value={message} placeholder="Message the office or resident" onkeydown={(event) => { if (event.key === 'Enter' && message.trim()) messageMutation.mutate(); }} /><Button size="icon" aria-label="Send message" disabled={!message.trim() || messageMutation.isPending} onclick={() => messageMutation.mutate()}><Send class="h-4 w-4" /></Button></div>
			</section>{:else}<section class="h-fit rounded-2xl border border-border bg-card p-5"><h2 class="flex items-center gap-2 font-semibold"><MessageSquare class="h-4 w-4" />Assignment conversation</h2><p class="mt-2 text-sm text-muted-foreground">Messaging is not included in this assignment.</p></section>{/if}
		</div>
	{/if}
</div>
