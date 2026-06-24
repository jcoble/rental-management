import type { UserRole } from '../../../../lib/types/index.ts';

export interface InviteMemberForm {
	email: string;
	displayName: string;
	role: UserRole;
	tenantId: string;
	temporaryPassword: string;
}

export interface CreateTeamMemberBody {
	email: string;
	displayName?: string;
	role: UserRole;
	tenantId?: number;
	temporaryPassword?: string;
}

export function createEmptyInviteMemberForm(): InviteMemberForm {
	return {
		email: '',
		displayName: '',
		role: 'Manager',
		tenantId: '',
		temporaryPassword: '',
	};
}

export function inviteMemberSubmitDisabled(form: InviteMemberForm, isPending: boolean): boolean {
	if (isPending) return true;
	if (!form.email.trim()) return true;
	if (form.role === 'Tenant' && parseTenantId(form.tenantId) == null) return true;
	return false;
}

export function buildCreateTeamMemberBody(form: InviteMemberForm): CreateTeamMemberBody {
	const body: CreateTeamMemberBody = {
		email: form.email.trim(),
		role: form.role,
	};

	const displayName = form.displayName.trim();
	if (displayName) body.displayName = displayName;

	const temporaryPassword = form.temporaryPassword.trim();
	if (temporaryPassword) body.temporaryPassword = temporaryPassword;

	const tenantId = parseTenantId(form.tenantId);
	if (form.role === 'Tenant' && tenantId != null) {
		body.tenantId = tenantId;
	}

	return body;
}

function parseTenantId(value: string): number | null {
	const parsed = Number(value);
	return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : null;
}
