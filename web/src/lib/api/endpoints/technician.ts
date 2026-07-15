import { api } from "../client";
import { idempotentMutation } from "../idempotency";
import { buildListQuery, type ListParams } from "../list-params";

export type TechnicianWorkOrderStatus =
  | "New"
  | "Scheduled"
  | "InProgress"
  | "WaitingParts"
  | "OnHold"
  | "Completed"
  | "Cancelled"
  | "Archived";

export type TechnicianWorkEntryKind = "Note" | "Time" | "Material" | "Photo";

export interface TechnicianAssignmentListItem {
  id: number;
  title: string;
  category: string;
  status: TechnicianWorkOrderStatus;
  address: string;
  unit?: string | null;
  scheduledForUtc?: string | null;
  scheduledWindowEndUtc?: string | null;
  updatedAtUtc: string;
  unreadMessageCount: number;
}

export interface TechnicianAssignmentPage {
  items: TechnicianAssignmentListItem[];
  totalCount: number;
  skip: number;
  take: number;
}

export interface TechnicianTimelineItem {
  id: number;
  fromStatus?: TechnicianWorkOrderStatus | null;
  toStatus: TechnicianWorkOrderStatus;
  note?: string | null;
  changedBy?: string | null;
  createdAtUtc: string;
}

export interface TechnicianWorkEntry {
  id: number;
  kind: TechnicianWorkEntryKind;
  note?: string | null;
  quantity?: number | null;
  unit?: string | null;
  photoFileId?: number | null;
  occurredAtUtc: string;
  createdAtUtc: string;
}

export interface TechnicianConversationMessage {
  id: number;
  sender: "You" | "Tenant" | "Office";
  body: string;
  createdAtUtc: string;
}

export interface TechnicianAssignmentDetail {
  id: number;
  title: string;
  category: string;
  status: TechnicianWorkOrderStatus;
  address: string;
  unit?: string | null;
  scheduledForUtc?: string | null;
  scheduledWindowEndUtc?: string | null;
  updatedAtUtc: string;
  description: string;
  requestedAtUtc: string;
  completedAtUtc?: string | null;
  accessInstructions?: string | null;
  contactName?: string | null;
  contactPhone?: string | null;
  contactEmail?: string | null;
  conversationId?: number | null;
  timeline: TechnicianTimelineItem[];
  entries: TechnicianWorkEntry[];
  messages: TechnicianConversationMessage[];
}

export interface TechnicianAssignmentParams extends ListParams {
  openOnly?: boolean;
  status?: TechnicianWorkOrderStatus;
  scheduledFrom?: string;
  scheduledTo?: string;
}

function listQuery(params?: TechnicianAssignmentParams): string {
  const { openOnly, status, scheduledFrom, scheduledTo, ...list } =
    params ?? {};
  return buildListQuery(list, {
    openOnly: openOnly == null ? undefined : String(openOnly),
    status,
    scheduledFrom,
    scheduledTo,
  });
}

export const technician = {
  assignments: (params?: TechnicianAssignmentParams) =>
    api.get<TechnicianAssignmentPage>(
      `/technician/assignments${listQuery(params)}`
    ),
  schedule: (params?: TechnicianAssignmentParams) =>
    api.get<TechnicianAssignmentPage>(
      `/technician/schedule${listQuery(params)}`
    ),
  inbox: (params?: TechnicianAssignmentParams) =>
    api.get<TechnicianAssignmentPage>(`/technician/inbox${listQuery(params)}`),
  detail: (id: number) =>
    api.get<TechnicianAssignmentDetail>(`/technician/assignments/${id}`),
  recordEntry: (
    id: number,
    data: {
      kind: TechnicianWorkEntryKind;
      note?: string;
      quantity?: number;
      unit?: string;
      photoFileId?: number;
      occurredAt?: string;
    }
  ) =>
    idempotentMutation(
      `technician:entry:${id}:${JSON.stringify(data)}`,
      (key) =>
        api.post(`/technician/assignments/${id}/entries`, data, {
          headers: { "Idempotency-Key": key },
        })
    ),
  sendMessage: (id: number, body: string) =>
    idempotentMutation(`technician:message:${id}:${body}`, (key) =>
      api.post(
        `/technician/assignments/${id}/messages`,
        { body },
        {
          headers: { "Idempotency-Key": key },
        }
      )
    ),
  markConversationRead: (id: number) =>
    idempotentMutation(`technician:conversation-read:${id}`, (key) =>
      api.post(`/technician/assignments/${id}/conversation/read`, undefined, {
        headers: { "Idempotency-Key": key },
      })
    ),
};
