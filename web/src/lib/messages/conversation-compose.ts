export const CONVERSATION_SUBJECT_MAX_LENGTH = 200;

export function conversationSubjectError(subject: string): string | null {
	return subject.trim().length > CONVERSATION_SUBJECT_MAX_LENGTH
		? `Topic must be ${CONVERSATION_SUBJECT_MAX_LENGTH} characters or fewer`
		: null;
}
