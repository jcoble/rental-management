/**
 * The unit-page actions that open in the Command Drawer (spec section 7). The header New menu, the tab
 * action buttons, and the rail's next-best-action all request one of these; the page hosts the drawer
 * and renders the matching form with the unit context preloaded.
 */
export type UnitDrawerAction =
	| 'post-payment'
	| 'add-expense'
	| 'create-work-order'
	| 'upload-document'
	| 'scan';
