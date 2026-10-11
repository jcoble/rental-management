import type { Owner } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';
import { idempotentMutation } from '../idempotency';

export interface OwnerListResponse {
	items: Owner[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface ActivateOwnerPortalAccessResponse {
	outcome: 'Activated' | 'InvitationPending' | 'AlreadyActive' | 'MissingOwnerEmail' | 'MissingUserAccount' | 'MissingWorkspaceAccess' | 'InactiveWorkspaceAccess' | 'PrimaryOwnerNotSupported' | 'NotFound' | 'Invalid';
	ownerEntityId: number;
	ownerEmail?: string | null;
	targetAccessContextId?: number | null;
	ownerUserAccessId?: number | null;
	accessRevision?: number | null;
	replayed: boolean;
	requiresAccountActivation: boolean;
	invitationExpiresAtUtc?: string | null;
	message: string;
}

export interface RevokeOwnerPortalAccessResponse {
	outcome: 'Revoked' | 'AlreadyRevoked' | 'NotFound';
	ownerEntityId: number;
	ownerEmail?: string | null;
	revokedRelationshipCount: number;
	targetAccessContextId?: number | null;
	ownerUserAccessId?: number | null;
	accessRevision?: number | null;
	replayed: boolean;
	message: string;
}

export const owners = {
	list: (portfolioId: number, params?: ListParams) =>
		api.get<Owner[]>(`/owner-entities${buildListQuery(params, { portfolioId })}`),
	listPage: (portfolioId: number, params?: ListParams) =>
		api.get<OwnerListResponse>(`/owner-entities/page${buildListQuery(params, { portfolioId })}`),
	get: (id: number) => api.get<Owner>(`/owner-entities/${id}`),
	create: (data: Record<string, unknown>) =>
		idempotentMutation(`owners:create:${JSON.stringify(data)}`, (key) =>
			api.post<Owner>('/owner-entities', data, { headers: { 'Idempotency-Key': key } })
		),
	update: (id: number, data: Record<string, unknown>) =>
		idempotentMutation(`owners:update:${id}:${JSON.stringify(data)}`, (key) =>
			api.patch<Owner>(`/owner-entities/${id}`, data, { headers: { 'Idempotency-Key': key } })
		),
	delete: (id: number) =>
		idempotentMutation(`owners:delete:${id}`, (key) =>
			api.delete(`/owner-entities/${id}`, {
				headers: { 'Idempotency-Key': key }
			})
		),
	activatePortalAccess: (id: number) =>
		idempotentMutation(`owners:portal-access:activate:${id}`, (key) =>
			api.post<ActivateOwnerPortalAccessResponse>(`/owner-entities/${id}/portal-access/activate`, {}, {
				headers: { 'Idempotency-Key': key }
			})
		),
	revokePortalAccess: (id: number) =>
		idempotentMutation(`owners:portal-access:revoke:${id}`, (key) =>
			api.post<RevokeOwnerPortalAccessResponse>(`/owner-entities/${id}/portal-access/revoke`, {}, {
				headers: { 'Idempotency-Key': key }
			})
		),
	emailStatement: (ownerId: number, year: number) =>
		idempotentMutation(`owner-statement:email:${ownerId}:${year}`, (key) =>
			api.post<void>(`/accounting/owner-statements/${ownerId}/email?year=${year}`, {}, {
				headers: { 'Idempotency-Key': key }
			})
		),
};
