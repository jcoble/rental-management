import { redirect } from '@sveltejs/kit';

// Vendors split out of the Owners directory (IA Wave 1). The bare /owners/vendors index
// (with no id) otherwise falls through to the /owners/[id] route as id="vendors" and
// renders a confusing "Owner not found" page. Send it to the Vendors list instead so
// old links/bookmarks resolve. The id form is handled by ./[id]/+page.ts.
export function load() {
	redirect(307, '/vendors');
}
