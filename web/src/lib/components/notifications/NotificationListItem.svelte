<script lang="ts">
	import { goto } from '$app/navigation';
	import { Info, CheckCircle2, AlertTriangle, XCircle } from '@lucide/svelte';
	import type { NotificationItem, NotificationSeverity } from '$lib/api/types/notification';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { notificationIntentUrl } from '$lib/utils/portalLinks';

	type Props = {
		notification: NotificationItem;
		truncate?: boolean;
		onRead?: (id: number) => void;
	};

	let { notification, truncate = true, onRead }: Props = $props();
	const authState = getAuthState();

	const severityConfig: Record<
		NotificationSeverity,
		{ border: string; icon: typeof Info; iconColor: string; bg: string }
	> = {
		Info: { border: 'border-l-[var(--info)]', icon: Info, iconColor: 'text-[var(--info)]', bg: 'bg-[color-mix(in_srgb,var(--info)_8%,transparent)]' },
		Success: { border: 'border-l-[var(--success)]', icon: CheckCircle2, iconColor: 'text-[var(--success)]', bg: 'bg-[color-mix(in_srgb,var(--success)_8%,transparent)]' },
		Warning: { border: 'border-l-[var(--warning)]', icon: AlertTriangle, iconColor: 'text-[var(--warning)]', bg: 'bg-[color-mix(in_srgb,var(--warning)_8%,transparent)]' },
		Error: { border: 'border-l-[var(--m3c-error)]', icon: XCircle, iconColor: 'text-[var(--m3c-error)]', bg: 'bg-[color-mix(in_srgb,var(--m3c-error)_8%,transparent)]' },
		Critical: { border: 'border-l-[var(--m3c-error)]', icon: XCircle, iconColor: 'text-[var(--m3c-error)]', bg: 'bg-[color-mix(in_srgb,var(--m3c-error)_10%,transparent)]' }
	};

	const config = $derived(severityConfig[notification.severity] || severityConfig.Info);

	function formatRelativeTime(date: string): string {
		const value = new Date(date).getTime();
		if (Number.isNaN(value)) return '';
		const seconds = Math.max(0, Math.floor((Date.now() - value) / 1000));
		if (seconds < 60) return 'now';
		const minutes = Math.floor(seconds / 60);
		if (minutes < 60) return `${minutes}m ago`;
		const hours = Math.floor(minutes / 60);
		if (hours < 24) return `${hours}h ago`;
		const days = Math.floor(hours / 24);
		return `${days}d ago`;
	}

	async function handleClick() {
		if (!notification.isRead) onRead?.(notification.id);
		await goto(
			notificationIntentUrl(
				notification.navigationIntent,
				authState.accessEnvelope?.selectedContext
			),
			{ invalidateAll: true }
		);
	}
</script>

<button
	type="button"
	class="flex w-full items-start gap-3 border-l-2 px-3 py-2.5 text-left transition-colors hover:bg-accent/50
		{notification.isRead ? 'border-l-transparent' : config.border}
		{notification.isRead ? '' : config.bg}"
	onclick={handleClick}
	data-testid="notification-item-{notification.id}"
>
	<div class="mt-0.5 shrink-0">
		<config.icon class="h-4 w-4 {config.iconColor}" />
	</div>
	<div class="min-w-0 flex-1">
		<div class="flex items-center justify-between gap-2">
			<span class="text-sm {notification.isRead ? 'font-normal text-muted-foreground' : 'font-semibold text-foreground'}">
				{notification.title}
			</span>
			<span class="shrink-0 text-xs text-muted-foreground">{formatRelativeTime(notification.createdAt)}</span>
		</div>
		<p class="mt-0.5 text-xs text-muted-foreground {truncate ? 'line-clamp-2' : ''}">
			{notification.message}
		</p>
	</div>
</button>
