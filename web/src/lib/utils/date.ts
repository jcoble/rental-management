export function formatDate(date: string | Date | null | undefined): string {
	if (!date) return '';
	const d = new Date(date);
	return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
}

export function formatRelative(date: string | Date | null | undefined): string {
	if (!date) return '';
	const d = new Date(date);
	const now = new Date();
	const diff = now.getTime() - d.getTime();
	const minutes = Math.floor(diff / 60000);
	if (minutes < 1) return 'just now';
	if (minutes < 60) return `${minutes}m ago`;
	const hours = Math.floor(minutes / 60);
	if (hours < 24) return `${hours}h ago`;
	const days = Math.floor(hours / 24);
	if (days < 7) return `${days}d ago`;
	return formatDate(date);
}
