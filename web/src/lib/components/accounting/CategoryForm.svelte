<script lang="ts">
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import type {
		ChartOfAccountsRow,
		ScheduleECategory
	} from '$lib/api/endpoints/accounting-books';
	import { EXPENSE_CATEGORY_OPTIONS, formatExpenseCategory } from '$lib/accounting/expense-categories';
	import {
		categoryParentOptions,
		type CategoryDraft,
		type CategoryKind
	} from '$lib/accounting/chart-of-accounts-state';

	const NONE_VALUE = 'none';

	let {
		open = false,
		kind = 'Income',
		account = null,
		accounts = [],
		busy = false,
		onsave,
		oncancel
	}: {
		open?: boolean;
		kind?: CategoryKind;
		account?: ChartOfAccountsRow | null;
		accounts?: ChartOfAccountsRow[];
		busy?: boolean;
		onsave?: (draft: CategoryDraft) => void;
		oncancel?: () => void;
	} = $props();

	let name = $state('');
	let scheduleValue = $state(NONE_VALUE);
	let parentValue = $state(NONE_VALUE);
	let formError = $state('');

	const editing = $derived(account !== null);
	const effectiveKind = $derived(account?.accountType === 'Expense' ? 'Expense' : kind);
	const parentOptions = $derived(categoryParentOptions(accounts, effectiveKind, account?.id ?? null));
	const selectedScheduleLabel = $derived(
		scheduleValue === NONE_VALUE
			? 'No Schedule E category'
			: formatExpenseCategory(scheduleValue)
	);
	const selectedParentLabel = $derived(
		parentValue === NONE_VALUE
			? 'No parent category'
			: parentOptions.find((parent) => String(parent.id) === parentValue)?.name ?? 'Choose a parent category'
	);

	$effect(() => {
		if (!open) return;

		name = account?.name ?? '';
		scheduleValue = account?.scheduleECategory ?? NONE_VALUE;
		parentValue = account?.parentAccountId === null || account?.parentAccountId === undefined
			? NONE_VALUE
			: String(account.parentAccountId);
		formError = '';
	});

	function submit(event: SubmitEvent): void {
		event.preventDefault();
		formError = '';

		const trimmedName = name.trim();
		if (!trimmedName) {
			formError = 'Enter a category name.';
			return;
		}

		const parentAccountId = parentValue === NONE_VALUE ? null : Number(parentValue);
		if (parentAccountId !== null && !Number.isInteger(parentAccountId)) {
			formError = 'Choose a valid parent category.';
			return;
		}

		const scheduleECategory =
			effectiveKind === 'Expense' && scheduleValue !== NONE_VALUE
				? (scheduleValue as ScheduleECategory)
				: null;

		onsave?.({
			name: trimmedName,
			scheduleECategory,
			parentAccountId
		});
	}
</script>

<Dialog.Root {open} onOpenChange={(value) => { if (!value) oncancel?.(); }}>
	<Dialog.Content class="max-w-xl" data-testid="category-form-dialog">
		<Dialog.Header>
			<Dialog.Title>
				{editing ? `Rename ${effectiveKind.toLowerCase()} category` : `Add ${effectiveKind.toLowerCase()} category`}
			</Dialog.Title>
			<Dialog.Description>
				Use a clear name. Rental Command assigns the remaining accounting details automatically.
			</Dialog.Description>
		</Dialog.Header>

		<form class="space-y-5" onsubmit={submit}>
			<div class="space-y-2">
				<label for="category-name" class="text-sm font-medium text-foreground">Name</label>
				<Input
					id="category-name"
					value={name}
					oninput={(event) => { name = (event.currentTarget as HTMLInputElement).value; }}
					placeholder={effectiveKind === 'Income' ? 'e.g. Parking income' : 'e.g. Landscaping'}
					maxlength={120}
					disabled={busy}
					aria-invalid={Boolean(formError)}
				/>
			</div>

			{#if effectiveKind === 'Expense'}
				<div class="space-y-2">
					<label for="category-schedule-e" class="text-sm font-medium text-foreground">Schedule E category</label>
					<Select.Root type="single" bind:value={scheduleValue} disabled={busy}>
						<Select.Trigger id="category-schedule-e" class="w-full">
							{selectedScheduleLabel}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value={NONE_VALUE} label="No Schedule E category">No Schedule E category</Select.Item>
							{#each EXPENSE_CATEGORY_OPTIONS as option (option.value)}
								<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
				</div>
			{/if}

			<div class="space-y-2">
				<label for="category-parent" class="text-sm font-medium text-foreground">Parent category <span class="font-normal text-muted-foreground">(optional)</span></label>
				<Select.Root type="single" bind:value={parentValue} disabled={busy}>
					<Select.Trigger id="category-parent" class="w-full">
						{selectedParentLabel}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value={NONE_VALUE} label="No parent category">No parent category</Select.Item>
						{#each parentOptions as parent (parent.id)}
							<Select.Item value={String(parent.id)} label={parent.name}>{parent.name}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>

			{#if formError}
				<p class="text-sm text-destructive" role="alert">{formError}</p>
			{/if}

			<Dialog.Footer>
				<Button type="button" variant="outline" onclick={oncancel} disabled={busy}>Cancel</Button>
				<Button type="submit" disabled={busy}>{busy ? 'Saving…' : editing ? 'Save changes' : 'Add category'}</Button>
			</Dialog.Footer>
		</form>
	</Dialog.Content>
</Dialog.Root>
