import { env } from '$env/dynamic/private';
import { error, json } from '@sveltejs/kit';

import type { RequestHandler } from './$types';

export const GET: RequestHandler = () => {
	const fingerprints = (env.MOBILE_ANDROID_SHA256_CERT_FINGERPRINTS ?? '')
		.split(/[,\s]+/)
		.map((value) => value.trim())
		.filter(Boolean);

	if (fingerprints.length === 0) {
		throw error(404, 'Android App Links are not configured.');
	}

	return json(
		[
			{
				relation: ['delegate_permission/common.handle_all_urls'],
				target: {
					namespace: 'android_app',
					package_name:
						env.MOBILE_ANDROID_PACKAGE_NAME?.trim() ||
						'com.rentalcommand.rental_command',
					sha256_cert_fingerprints: fingerprints
				}
			}
		],
		{
			headers: {
				'cache-control': 'public, max-age=3600'
			}
		}
	);
};
