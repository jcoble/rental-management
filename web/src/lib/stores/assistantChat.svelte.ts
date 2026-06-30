import {
	ai,
	type AskDelivery,
	type AssistantActionDraft,
	type AssistantActionDraftResponse,
	type QaTurn
} from '$lib/api/endpoints/ai';
import { looksLikeAssistantActionCommand } from '$lib/assistant/actions';

export type AssistantChatMessage = {
	id: string;
	role: 'user' | 'assistant';
	content: string;
	isLoading?: boolean;
	isError?: boolean;
	/** For assistant messages: the question that produced it, so it can be re-delivered. */
	question?: string;
	/** Channel currently being delivered ('email' | 'sms'), or null. */
	deliveringChannel?: 'email' | 'sms' | null;
	/** Short status note shown under the message after a delivery attempt. */
	deliveryNote?: string | null;
	actionDraft?: AssistantActionDraft | null;
	actionStatus?: AssistantActionDraftResponse['status'] | null;
	actionMissingFields?: string[];
	actionNote?: string | null;
	isExecutingAction?: boolean;
};

class AssistantChatStore {
	isOpen = $state(false);
	isLoading = $state(false);
	writeModeEnabled = $state(false);
	error = $state<string | null>(null);
	messages = $state<AssistantChatMessage[]>([]);

	open() {
		this.isOpen = true;
	}

	close() {
		this.isOpen = false;
	}

	toggle() {
		this.isOpen = !this.isOpen;
	}

	clear() {
		this.messages = [];
		this.error = null;
	}

	setWriteMode(enabled: boolean) {
		this.writeModeEnabled = enabled;
	}

	async send(message: string) {
		const text = message.trim();
		if (!text || this.isLoading) return;

		const userMessage: AssistantChatMessage = {
			id: crypto.randomUUID(),
			role: 'user',
			content: text,
		};
		const assistantMessage: AssistantChatMessage = {
			id: crypto.randomUUID(),
			role: 'assistant',
			content: '',
			isLoading: true,
			question: text,
		};

		const history: QaTurn[] = this.messages
			.filter((m) => !m.isLoading && !m.isError)
			.slice(-8)
			.map((m) => ({ role: m.role, content: m.content }));

		this.messages = [...this.messages, userMessage, assistantMessage];
		this.error = null;
		this.isLoading = true;

		try {
			if (looksLikeAssistantActionCommand(text)) {
				const response = await ai.draftAction(text, this.writeModeEnabled);
				this.messages = this.messages.map((m) =>
					m.id === assistantMessage.id
						? {
								...m,
								content: response.message,
								isLoading: false,
								actionDraft: response.draft,
								actionStatus: response.status,
								actionMissingFields: response.missingFields,
							}
						: m
				);
			} else {
				const response = await ai.ask(text, history);
				this.messages = this.messages.map((m) =>
					m.id === assistantMessage.id
						? { ...m, content: response.answer, isLoading: false }
						: m
				);
			}
		} catch (err) {
			const message = err instanceof Error ? err.message : 'Assistant failed to respond.';
			this.error = message;
			this.messages = this.messages.map((m) =>
				m.id === assistantMessage.id
					? { ...m, content: message, isLoading: false, isError: true }
					: m
			);
		} finally {
			this.isLoading = false;
		}
	}

	async executeAction(messageId: string) {
		const target = this.messages.find((m) => m.id === messageId);
		if (!target?.actionDraft || target.isExecutingAction) return;

		this.#patch(messageId, { isExecutingAction: true, actionNote: null });

		try {
			const response = await ai.executeAction(target.actionDraft, this.writeModeEnabled);
			this.#patch(messageId, {
				content: response.message,
				actionStatus: response.status,
				actionNote: response.detailHref ? `Open: ${response.detailHref}` : null,
				isExecutingAction: false,
				actionDraft: response.status === 'Created' ? null : target.actionDraft,
			});
		} catch (err) {
			const note = err instanceof Error ? err.message : 'Action failed.';
			this.#patch(messageId, { isExecutingAction: false, actionNote: note });
		}
	}

	/**
	 * Re-runs the question that produced an assistant message with delivery turned on, so the
	 * landlord gets the answer as a text or email. Uses the existing /ai/ask endpoint (the server
	 * enqueues an email/SMS outbox row after answering). Shows a transient per-message status.
	 */
	async deliver(messageId: string, channel: 'email' | 'sms') {
		const target = this.messages.find((m) => m.id === messageId);
		if (!target || target.role !== 'assistant' || !target.question || target.deliveringChannel) {
			return;
		}

		const question = target.question;
		const history: QaTurn[] = this.messages
			.filter((m) => !m.isLoading && !m.isError && m.id !== messageId)
			.slice(-8)
			.map((m) => ({ role: m.role, content: m.content }));

		const delivery: AskDelivery =
			channel === 'email' ? { deliverViaEmail: true } : { deliverViaSms: true };

		this.#patch(messageId, { deliveringChannel: channel, deliveryNote: null });

		try {
			const response = await ai.ask(question, history, delivery);
			const delivered = response.deliveredChannels ?? [];
			const ok =
				channel === 'email'
					? delivered.includes('Email')
					: delivered.includes('Sms');
			const note = ok
				? channel === 'email'
					? 'Emailed to you.'
					: 'Texted to you.'
				: channel === 'email'
					? "Couldn't email — no address on file."
					: "Couldn't text — no phone on file.";
			this.#patch(messageId, { deliveringChannel: null, deliveryNote: note });
		} catch (err) {
			const note = err instanceof Error ? err.message : 'Delivery failed.';
			this.#patch(messageId, { deliveringChannel: null, deliveryNote: note });
		}
	}

	#patch(messageId: string, changes: Partial<AssistantChatMessage>) {
		this.messages = this.messages.map((m) => (m.id === messageId ? { ...m, ...changes } : m));
	}
}

export const assistantChat = new AssistantChatStore();
