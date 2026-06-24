export function formatLeaseAnswerText(answer: string | null | undefined): string {
	return (answer ?? '')
		.replace(/\*\*([^*\n]+)\*\*/g, '$1')
		.replace(/__([^_\n]+)__/g, '$1')
		.replace(/`([^`\n]+)`/g, '$1');
}
