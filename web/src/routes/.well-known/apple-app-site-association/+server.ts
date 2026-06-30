import { env } from '$env/dynamic/private';
import { error, json } from '@sveltejs/kit';

import type { RequestHandler } from './$types';

export const GET: RequestHandler = () => {
	const appId = env.MOBILE_APPLE_APP_ID?.trim();
	if (!appId) {
		throw error(404, 'Apple Universal Links are not configured.');
	}

	return json(
		{
			applinks: {
				apps: [],
				details: [
					{
						appIDs: [appId],
						components: [
							{
								'/': '/reset-password',
								comment: 'Open emailed password reset links in the Rental Command app.'
							},
							{
								'/': '/verify-email',
								comment: 'Open emailed email-verification links in the Rental Command app.'
							}
						]
					}
				]
			}
		},
		{
			headers: {
				'cache-control': 'public, max-age=3600'
			}
		}
	);
};
