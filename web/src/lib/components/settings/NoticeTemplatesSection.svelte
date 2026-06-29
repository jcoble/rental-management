<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { notices } from '$lib/api/endpoints/notices';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Card from '$lib/components/ui/card';
	import { ChevronDown, ChevronUp } from '@lucide/svelte';
	import type { NoticeTemplateResponse } from '$lib/types';
	import { insertFieldToken } from './insert-field-token';

	// The five autoSend* booleans live on the page's notificationSettingsForm. We mutate them in place
	// (shared reactive proxy) so the page's EXISTING "Save notification settings" flow persists them in
	// its PUT payload — this section does not own a separate settings save path.
	type SendModeFields = {
		autoSendRentReminder: boolean;
		autoSendRenewal: boolean;
		autoSendMonthToMonth: boolean;
		autoSendMoveOut: boolean;
		autoSendLateRent: boolean;
	};

	let {
		settings,
		onSaveSettings,
		savingSettings = false,
		settingsDisabled = false
	}: {
		settings: SendModeFields;
		onSaveSettings: () => void;
		savingSettings?: boolean;
		settingsDisabled?: boolean;
	} = $props();

	// Notice types in canonical backend order, with the landlord-friendly label and the matching
	// autoSend* field on the settings form.
	const typeConfigs: { type: string; label: string; field: keyof SendModeFields }[] = [
		{ type: 'RentReminder', label: 'Rent reminder (coming due)', field: 'autoSendRentReminder' },
		{ type: 'RenewalOffer', label: 'Lease renewal offer', field: 'autoSendRenewal' },
		{
			type: 'MonthToMonthConversion',
			label: 'Convert to month-to-month',
			field: 'autoSendMonthToMonth'
		},
		{ type: 'MoveOutReminder', label: 'Lease expiration · move-out', field: 'autoSendMoveOut' },
		{ type: 'LateRentNotice', label: 'Late rent · late fee', field: 'autoSendLateRent' }
	];

	const portfolioId = $derived(getCurrentPortfolioId());
	const queryClient = useQueryClient();

	const templatesQuery = createQuery(() => ({
		queryKey: ['notice-templates', portfolioId],
		queryFn: () => notices.templates.list(),
		enabled: portfolioId != null
	}));

	// Editable draft (subject/body) per type, seeded from the server templates. Kept separate from the
	// query data so typing doesn't fight refetches.
	let drafts = $state<Record<string, { subject: string; body: string }>>({});
	let seededFor = $state<NoticeTemplateResponse[] | null>(null);
	let expandedType = $state<string | null>(null);

	$effect(() => {
		const data = templatesQuery.data;
		if (!data || data === seededFor) return;
		const next: Record<string, { subject: string; body: string }> = {};
		for (const t of data) {
			next[t.noticeType] = { subject: t.subject ?? '', body: t.body ?? '' };
		}
		drafts = next;
		seededFor = data;
	});

	function templateFor(type: string): NoticeTemplateResponse | undefined {
		return templatesQuery.data?.find((t) => t.noticeType === type);
	}

	// Track the field the landlord last focused inside the expanded card so an "insert field" button
	// drops the token at the caret in the right element.
	let subjectEl = $state<HTMLInputElement | null>(null);
	let bodyEl = $state<HTMLTextAreaElement | null>(null);
	let focusedField = $state<'subject' | 'body'>('body');

	function insertInto(type: string, field: string) {
		const draft = drafts[type];
		if (!draft) return;
		const el = focusedField === 'subject' ? subjectEl : bodyEl;
		const caret = el ? (el.selectionStart ?? (focusedField === 'subject' ? draft.subject : draft.body).length) : (focusedField === 'subject' ? draft.subject : draft.body).length;
		const current = focusedField === 'subject' ? draft.subject : draft.body;
		const result = insertFieldToken(current, caret, field);
		if (focusedField === 'subject') {
			draft.subject = result.text;
		} else {
			draft.body = result.text;
		}
		// Restore focus + caret after the DOM updates.
		queueMicrotask(() => {
			if (el) {
				el.focus();
				el.setSelectionRange(result.caret, result.caret);
			}
		});
	}

	const saveTemplateMutation = createMutation(() => ({
		mutationFn: (type: string) =>
			notices.templates.upsert(type, {
				subject: drafts[type]?.subject ?? '',
				body: drafts[type]?.body ?? ''
			}),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['notice-templates', portfolioId] });
			showSuccess('Template saved.');
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function toggle(type: string) {
		expandedType = expandedType === type ? null : type;
	}
</script>

<Card.Root class="gap-0 py-0" data-testid="settings-notice-templates">
	<Card.Content class="p-5">
		<p class="text-sm font-semibold">Notice messages &amp; auto-send</p>
		<p class="mb-3 text-xs text-muted-foreground">
			Choose whether each kind of notice is sent automatically or held for you to review first, and
			edit the message the tenant receives. Use the insert buttons to drop in fields that fill
			themselves in.
		</p>

		{#if templatesQuery.isLoading}
			<p class="text-sm text-muted-foreground">Loading templates…</p>
		{:else if templatesQuery.isError}
			<p class="text-sm text-destructive">Couldn't load notice templates.</p>
		{:else}
			<div class="space-y-3">
				{#each typeConfigs as cfg (cfg.type)}
					{@const tpl = templateFor(cfg.type)}
					{@const autoSend = settings[cfg.field]}
					{@const draft = drafts[cfg.type]}
					<div
						class="rounded-lg border border-border"
						data-testid={`settings-notice-template-${cfg.type}`}
					>
						<div class="flex flex-wrap items-center justify-between gap-3 p-4">
							<div>
								<p class="font-medium leading-tight">{cfg.label}</p>
								{#if autoSend && tpl && !tpl.hasTemplate}
									<p
										class="mt-1 text-xs text-amber-600 dark:text-amber-500"
										data-testid={`settings-notice-template-${cfg.type}-needs-template`}
									>
										Add a template to enable auto-send.
									</p>
								{/if}
							</div>

							<div class="flex items-center gap-3">
								<div
									class="inline-flex overflow-hidden rounded-md border border-border"
									data-testid={`settings-notice-sendmode-${cfg.type}`}
								>
									<button
										type="button"
										class={`px-3 py-1.5 text-xs font-medium ${autoSend ? 'bg-primary text-primary-foreground' : 'bg-background text-muted-foreground'}`}
										onclick={() => (settings[cfg.field] = true)}
										data-testid={`settings-notice-sendmode-${cfg.type}-auto`}
									>
										Auto-send
									</button>
									<button
										type="button"
										class={`px-3 py-1.5 text-xs font-medium ${autoSend ? 'bg-background text-muted-foreground' : 'bg-primary text-primary-foreground'}`}
										onclick={() => (settings[cfg.field] = false)}
										data-testid={`settings-notice-sendmode-${cfg.type}-ask`}
									>
										Ask first
									</button>
								</div>
								<Button
									variant="outline"
									size="sm"
									onclick={() => toggle(cfg.type)}
									data-testid={`settings-notice-template-${cfg.type}-edit`}
								>
									{#if expandedType === cfg.type}
										<ChevronUp class="mr-1 size-4" /> Close
									{:else}
										<ChevronDown class="mr-1 size-4" /> Edit message
									{/if}
								</Button>
							</div>
						</div>

						{#if expandedType === cfg.type && draft}
							<div class="space-y-3 border-t border-border p-4">
								<div>
									<label
										for={`settings-notice-subject-${cfg.type}`}
										class="mb-1 block text-xs text-muted-foreground">Subject</label
									>
									<Input
										id={`settings-notice-subject-${cfg.type}`}
										bind:value={draft.subject}
										bind:ref={subjectEl}
										onfocus={() => (focusedField = 'subject')}
										data-testid={`settings-notice-subject-${cfg.type}`}
									/>
								</div>
								<div>
									<label
										for={`settings-notice-body-${cfg.type}`}
										class="mb-1 block text-xs text-muted-foreground">Message</label
									>
									<textarea
										id={`settings-notice-body-${cfg.type}`}
										bind:value={draft.body}
										bind:this={bodyEl}
										onfocus={() => (focusedField = 'body')}
										rows={6}
										class="w-full rounded border border-border bg-background px-3 py-2 text-sm"
										data-testid={`settings-notice-body-${cfg.type}`}
									></textarea>
								</div>

								{#if tpl && tpl.availableFields.length > 0}
									<div>
										<p class="mb-1 text-xs text-muted-foreground">Insert a field:</p>
										<div class="flex flex-wrap gap-2">
											{#each tpl.availableFields as field (field)}
												<button
													type="button"
													class="rounded-full border border-border bg-background px-2 py-0.5 text-xs hover:bg-muted"
													onclick={() => insertInto(cfg.type, field)}
													data-testid={`settings-notice-field-${cfg.type}-${field}`}
												>
													{`{{${field}}}`}
												</button>
											{/each}
										</div>
									</div>
								{/if}

								<div>
									<Button
										size="sm"
										onclick={() => saveTemplateMutation.mutate(cfg.type)}
										disabled={saveTemplateMutation.isPending}
										data-testid={`settings-notice-template-${cfg.type}-save`}
									>
										{saveTemplateMutation.isPending ? 'Saving…' : 'Save template'}
									</Button>
								</div>
							</div>
						{/if}
					</div>
				{/each}
			</div>

			<div class="mt-4 flex items-center gap-3">
				<Button
					onclick={() => onSaveSettings()}
					disabled={savingSettings || settingsDisabled}
					data-testid="settings-notice-sendmodes-save"
				>
					{savingSettings ? 'Saving…' : 'Save send modes'}
				</Button>
				<p class="text-xs text-muted-foreground">
					Auto-send / ask-first choices save with your notification settings.
				</p>
			</div>
		{/if}
	</Card.Content>
</Card.Root>
