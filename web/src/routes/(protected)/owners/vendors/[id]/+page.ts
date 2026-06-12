import { redirect } from '@sveltejs/kit';

// Vendors split out of the Owners directory (IA Wave 1). Vendor detail now lives
// under /vendors/[id]; keep old links/bookmarks working.
export function load({ params }) {
	redirect(307, `/vendors/${params.id}`);
}
