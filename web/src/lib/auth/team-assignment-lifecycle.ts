export interface TeamAssignmentLifecycleInput {
	status: string;
	effectiveToUtc: string | null;
}

export function isTeamAssignmentEffectivelyActive(
	assignment: TeamAssignmentLifecycleInput,
	asOfUtc: Date = new Date()
): boolean {
	if (assignment.status !== 'Active') return false;
	if (!assignment.effectiveToUtc) return true;

	const effectiveToMs = Date.parse(assignment.effectiveToUtc);
	if (Number.isNaN(effectiveToMs)) return true;

	return effectiveToMs > asOfUtc.getTime();
}

export function teamAssignmentDisplayStatus(
	assignment: TeamAssignmentLifecycleInput,
	asOfUtc: Date = new Date()
): string {
	if (assignment.status === 'Active' && !isTeamAssignmentEffectivelyActive(assignment, asOfUtc)) {
		return 'Ended';
	}

	return assignment.status;
}
