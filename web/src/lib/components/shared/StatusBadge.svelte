<script lang="ts">
	import { Badge } from '$lib/components/ui/badge/index.js';
	import { cn } from '$lib/utils.js';

	/**
	 * Default semantic color map keyed by status string.
	 *
	 * Values compose the app.css tone recipes (`m3-tone-chip` + a `m3-tone--*`
	 * modifier): the tone var flips with the active mode, so one class string is
	 * correct in BOTH dark and light. Don't add raw Tailwind palette literals
	 * (bg-green-100 / dark:text-green-400 ...) here — they're tuned for one mode
	 * and wash out in the other.
	 *
	 * Lifecycle hue mapping (rental analog of the EdiPlatform phase hues):
	 *   success = money-in / completed states · info = scheduled / in-flight ·
	 *   warning = partial / needs-attention · error = overdue / blocked ·
	 *   primary(violet) = review / held-in-trust · neutral = drafts & archive.
	 */
	const TONE = {
		success: 'm3-tone-chip border m3-tone--success',
		info: 'm3-tone-chip border m3-tone--info',
		warning: 'm3-tone-chip border m3-tone--warning',
		error: 'm3-tone-chip border m3-tone--error',
		primary: 'm3-tone-chip border m3-tone--primary',
		neutral: 'bg-muted text-muted-foreground border-border'
	} as const;

	const DEFAULT_MAP: Record<string, { label?: string; class: string }> = {
		// Positive / active
		Active: { class: TONE.success },
		Paid: { class: TONE.success },
		Approved: { class: TONE.success },
		Completed: { class: TONE.success },
		Done: { class: TONE.success },
		Confirmed: { class: TONE.success },
		Cleared: { class: TONE.success },
		Matched: { class: TONE.success },
		Signed: { class: TONE.success },
		Resolved: { class: TONE.success },
		// Informational / in-progress
		Pending: { class: TONE.info },
		Scheduled: { class: TONE.info },
		InProgress: { label: 'In Progress', class: TONE.info },
		Open: { class: TONE.info },
		Reviewing: { class: TONE.info },
		Returned: { class: TONE.info },
		New: { class: TONE.info },
		// Work-order priorities (Low/Normal stay calm on purpose)
		Low: { class: TONE.neutral },
		Normal: { class: TONE.neutral },
		High: { class: TONE.warning },
		Emergency: { class: TONE.error },
		// Review / held in trust (deposits) — violet reads as "set aside / not income"
		Review: { class: TONE.primary },
		Held: { class: TONE.primary },
		// Warning / partial
		Partial: { class: TONE.warning },
		Late: { class: TONE.warning },
		Waived: { class: TONE.warning },
		OnHold: { label: 'On Hold', class: TONE.warning },
		PendingSignature: { label: 'Pending Signature', class: TONE.warning },
		Expired: { class: TONE.warning },
		// Negative / alert
		Overdue: { class: TONE.error },
		Cancelled: { class: TONE.error },
		Blocked: { class: TONE.error },
		Declined: { class: TONE.error },
		Rejected: { class: TONE.error },
		Failed: { class: TONE.error },
		NoShow: { label: 'No Show', class: TONE.error },
		// Neutral
		Draft: { class: TONE.neutral },
		Archived: { class: TONE.neutral },
		Inactive: { class: TONE.neutral },
		Dismissed: { class: TONE.neutral },
		Closed: { class: TONE.neutral }
	};

	let {
		status,
		map,
		class: className
	}: {
		status: string;
		/** Override or extend the default color map. */
		map?: Record<string, { label?: string; class: string }>;
		class?: string;
	} = $props();

	const effectiveMap = $derived({ ...DEFAULT_MAP, ...(map ?? {}) });
	const entry = $derived(effectiveMap[status]);
	const label = $derived(entry?.label ?? status);
	const colorClass = $derived(entry?.class ?? TONE.neutral);
</script>

<Badge
	variant="outline"
	class={cn('text-[11px] font-medium px-2 py-0.5', colorClass, className)}
	data-testid="status-badge"
	data-status={status}
>
	{label}
</Badge>
