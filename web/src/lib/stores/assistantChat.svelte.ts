import { ai, type QaTurn } from '$lib/api/endpoints/ai';

export type AssistantChatMessage = {
	id: string;
	role: 'user' | 'assistant';
	content: string;
	isLoading?: boolean;
	isError?: boolean;
};

class AssistantChatStore {
	isOpen = $state(false);
	isLoading = $state(false);
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
		};

		const history: QaTurn[] = this.messages
			.filter((m) => !m.isLoading && !m.isError)
			.slice(-8)
			.map((m) => ({ role: m.role, content: m.content }));

		this.messages = [...this.messages, userMessage, assistantMessage];
		this.error = null;
		this.isLoading = true;

		try {
			const response = await ai.ask(text, history);
			this.messages = this.messages.map((m) =>
				m.id === assistantMessage.id
					? { ...m, content: response.answer, isLoading: false }
					: m
			);
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
}

export const assistantChat = new AssistantChatStore();
