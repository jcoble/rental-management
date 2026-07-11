import { redirect } from '@sveltejs/kit';
import type { PageLoad } from './$types';

// Vendors split out of the Owners directory (IA Wave 1). Vendor detail now lives
// under /vendors/[id]; keep old links/bookmarks working.
export const load: PageLoad = ({ params }) => {
	redirect(307, `/vendors/${params.id}`);
};
