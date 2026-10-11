import { error } from '@sveltejs/kit';
import { getBlogPost } from '$lib/blog/articles';
import type { PageServerLoad } from './$types';

export const load: PageServerLoad = ({ params }) => {
	const post = getBlogPost(params.slug);
	if (!post) throw error(404, 'That article could not be found.');
	return { post };
};
