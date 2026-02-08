let _projectId = $state(getInitialProjectId());

function getInitialProjectId(): number {
	if (typeof window === 'undefined') return 1;
	const stored = localStorage.getItem('lifecycle:currentProjectId');
	return stored ? parseInt(stored, 10) : 1;
}

export function getCurrentProjectId(): number {
	return _projectId;
}

export function setCurrentProjectId(id: number) {
	_projectId = id;
	if (typeof window !== 'undefined')
		localStorage.setItem('lifecycle:currentProjectId', String(id));
}
