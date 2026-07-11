import { redirect } from '@sveltejs/kit';
import type { PageLoad } from './$types';

// Work-order detail deduped (IA Wave 1, F4): /maintenance/[id] is canonical. This
// older duplicate now redirects so dashboard/briefing deep-links and bookmarks work.
export const load: PageLoad = ({ params }) => {
	redirect(307, `/maintenance/${params.id}`);
};
