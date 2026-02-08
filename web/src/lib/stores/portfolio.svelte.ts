let _portfolioId = $state(getInitialPortfolioId());

function getInitialPortfolioId(): number {
	if (typeof window === 'undefined') return 1;
	const stored = localStorage.getItem('rental:currentPortfolioId');
	return stored ? parseInt(stored, 10) : 1;
}

export function getCurrentPortfolioId(): number {
	return _portfolioId;
}

export function setCurrentPortfolioId(id: number) {
	_portfolioId = id;
	if (typeof window !== 'undefined')
		localStorage.setItem('rental:currentPortfolioId', String(id));
}
