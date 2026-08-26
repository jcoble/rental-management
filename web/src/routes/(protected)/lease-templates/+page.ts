import { redirect } from '@sveltejs/kit';

// Setting up the lease form is a once-a-year job, so it moved out of the daily
// Rentals list and into Settings (TSK-947 W12). Old bookmarks and links still work.
export function load() {
	redirect(307, '/settings/lease-templates');
}
