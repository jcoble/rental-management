import { redirect } from '@sveltejs/kit';

// The three notification settings pages are now sections of one page (TSK-947 W9).
// Keep old links and bookmarks working by landing on the matching section.
export function load() {
	redirect(307, '/settings/notifications#tenant-notices');
}
