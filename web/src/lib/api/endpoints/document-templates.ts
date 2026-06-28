import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export type DocumentTemplateKind = 'Lease' | 'RentalApplication' | 'Notice' | 'Other';
export type DocumentTemplateStatus = 'Draft' | 'Active' | 'Archived';
export type DocumentTemplateRenderMode = 'Overlay' | 'Restyle';
export type DocumentTemplateFieldKind =
	| 'Text'
	| 'Currency'
	| 'Date'
	| 'Signature'
	| 'Initials'
	| 'Checkbox'
	| 'DateSigned';
export type DocumentTemplateSignerRole = 'None' | 'Tenant' | 'Landlord' | 'CoSigner';

export interface DocumentTemplateField {
	id: number;
	documentTemplateId: number;
	fieldKey: string;
	label: string;
	kind: DocumentTemplateFieldKind;
	signerRole: DocumentTemplateSignerRole;
	pageNumber: number;
	xPct: number;
	yPct: number;
	widthPct: number;
	heightPct: number;
	required: boolean;
	locked: boolean;
	sortOrder: number;
	defaultText: string | null;
	testId: string;
}

export interface DocumentTemplate {
	id: number;
	portfolioId: number;
	kind: DocumentTemplateKind;
	status: DocumentTemplateStatus;
	renderMode: DocumentTemplateRenderMode;
	name: string;
	description: string | null;
	originalStoredFileId: number | null;
	compiledStoredFileId: number | null;
	hasDraftHtml: boolean;
	defaultForPortfolio: boolean;
	propertyId: number | null;
	version: number;
	fieldCount: number;
	createdAtUtc: string;
	updatedAtUtc: string;
	archivedAtUtc: string | null;
	fields: DocumentTemplateField[];
	testId: string;
}

export interface DocumentTemplateListParams extends ListParams {
	kind?: DocumentTemplateKind | '';
	status?: DocumentTemplateStatus | '';
}

export interface DocumentTemplateListResponse {
	items: DocumentTemplate[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface DocumentTemplateFieldCatalogItem {
	fieldKey: string;
	label: string;
	kind: DocumentTemplateFieldKind;
	signerRole: DocumentTemplateSignerRole;
	requiredForSignature: boolean;
	description: string;
}

function buildTemplateQuery(params?: DocumentTemplateListParams): string {
	return buildListQuery(params, {
		kind: params?.kind || undefined,
		status: params?.status || undefined,
	});
}

export const documentTemplates = {
	list: (params?: DocumentTemplateListParams) =>
		api.get<DocumentTemplate[]>(`/document-templates${buildTemplateQuery(params)}`),
	listPage: (params?: DocumentTemplateListParams) =>
		api.get<DocumentTemplateListResponse>(`/document-templates/page${buildTemplateQuery(params)}`),
	get: (id: number) => api.get<DocumentTemplate>(`/document-templates/${id}`),
	fieldCatalog: (kind: DocumentTemplateKind = 'Lease') =>
		api.get<DocumentTemplateFieldCatalogItem[]>(
			`/document-templates/field-catalog?kind=${encodeURIComponent(kind)}`
		),
	uploadLeasePdf: (file: File, values: {
		name?: string;
		description?: string;
		defaultForPortfolio?: boolean;
		propertyId?: number | null;
	}) => {
		const form = new FormData();
		form.append('file', file);
		if (values.name) form.append('name', values.name);
		if (values.description) form.append('description', values.description);
		if (values.defaultForPortfolio) form.append('defaultForPortfolio', 'true');
		if (values.propertyId != null) form.append('propertyId', String(values.propertyId));
		return api.upload<DocumentTemplate>('/document-templates/upload', form);
	},
};
