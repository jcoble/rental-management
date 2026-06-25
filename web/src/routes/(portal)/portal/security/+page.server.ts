import type { Actions } from './$types';
import { changePassword } from '$lib/server/account-security';

export const actions: Actions = {
	changePassword
};
