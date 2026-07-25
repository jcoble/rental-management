import type { Property, RentalStructure, Unit, UnitHealth } from "$lib/types";
import { api } from "../client";
import { idempotentMutation } from "../idempotency";
import { buildListQuery, type ListParams } from "../list-params";
import type {
  PropertyWithWorkspaceEntry,
  PropertyWorkspaceEntry,
} from "$lib/components/property/property-workspace";

export interface PropertyListParams extends ListParams {
  type?: string;
  status?: string;
}

export interface PropertyListResponse {
  items: PropertyWithWorkspaceEntry[];
  totalCount: number;
  skip: number;
  take: number;
}

export interface PropertyWorkspaceUnitPage {
  items: UnitHealth[];
  totalCount: number;
  skip: number;
  take: number;
}

export type PropertyWorkspaceDetail = Property & {
  workspaceEntry: PropertyWorkspaceEntry;
};

export interface SetupPropertyRequest {
  propertyId?: number;
  property: Record<string, unknown> & { rentalStructure: RentalStructure };
  units: Record<string, unknown>[];
}

export interface SetupPropertyResponse {
  property: Property;
  units: Unit[];
  updated: boolean;
}

export const properties = {
  list: (portfolioId: number, params?: ListParams) =>
    api.get<Property[]>(
      `/properties${buildListQuery(params, { portfolioId })}`
    ),
  listPage: (portfolioId: number, params?: PropertyListParams) =>
    api.get<PropertyListResponse>(
      `/properties/page${buildListQuery(params, {
        portfolioId,
        type: params?.type,
        status: params?.status,
      })}`
    ),
  get: (id: number) => api.get<PropertyWorkspaceDetail>(`/properties/${id}`),
  listWorkspaceUnitsPage: (propertyId: number, params?: ListParams) =>
    api.get<PropertyWorkspaceUnitPage>(
      `/units/list-with-health/page${buildListQuery(params, { propertyId })}`
    ),
  setup: (data: SetupPropertyRequest) =>
    idempotentMutation(`properties:setup:${JSON.stringify(data)}`, (key) =>
      api.post<SetupPropertyResponse>("/properties/setup", data, {
        headers: { "Idempotency-Key": key },
      })
    ),
  update: (id: number, data: Record<string, unknown>) =>
    idempotentMutation(
      `properties:update:${id}:${JSON.stringify(data)}`,
      (key) =>
        api.patch<Property>(`/properties/${id}`, data, {
          headers: { "Idempotency-Key": key },
        })
    ),
  delete: (id: number) =>
    idempotentMutation(`properties:delete:${id}`, (key) =>
      api.delete(`/properties/${id}`, { headers: { "Idempotency-Key": key } })
    ),
  listUnits: (propertyId: number) =>
    api.get<Unit[]>(`/units${buildListQuery(undefined, { propertyId })}`),
  createUnit: (propertyId: number, data: Record<string, unknown>) =>
    idempotentMutation(
      `units:create:${propertyId}:${JSON.stringify(data)}`,
      (operationKey) =>
        api.post<Unit>(
          "/units",
          { propertyId, ...data },
          {
            headers: { "Idempotency-Key": operationKey },
          }
        )
    ),
  updateUnit: (id: number, data: Record<string, unknown>) =>
    idempotentMutation(
      `units:update:${id}:${JSON.stringify(data)}`,
      (operationKey) =>
        api.patch<Unit>(`/units/${id}`, data, {
          headers: { "Idempotency-Key": operationKey },
        })
    ),
  deleteUnit: (id: number) =>
    idempotentMutation(`units:delete:${id}`, (operationKey) =>
      api.delete(`/units/${id}`, {
        headers: { "Idempotency-Key": operationKey },
      })
    ),
};
