import { redirect } from '@sveltejs/kit';

// Engine Health moved into the separate platform-operator shell (IA Wave 1, F6/TSK-212).
// Keep old /admin/engine links working; the super-admin layout enforces the email gate.
export function load() {
	redirect(307, '/superadmin/engine');
}
