import type { UserRole } from '../../../../lib/types/index.ts';

export interface InviteMemberForm {
	email: string;
	displayName: string;
	role: UserRole;
	temporaryPassword: string;
}

export interface CreateTeamMemberBody {
	email: string;
	displayName?: string;
	role: UserRole;
	temporaryPassword?: string;
}

export function createEmptyInviteMemberForm(): InviteMemberForm {
	return {
		email: '',
		displayName: '',
		role: 'Manager',
		temporaryPassword: '',
	};
}

export function inviteMemberSubmitDisabled(form: InviteMemberForm, isPending: boolean): boolean {
	if (isPending) return true;
	if (!form.email.trim()) return true;
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

	return body;
}
