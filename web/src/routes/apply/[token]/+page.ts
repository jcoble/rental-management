/**
 * The public apply page is a fully client-side, unauthenticated form. We render
 * it on the client (the apply data is loaded in-page via the public client) so
 * the page never depends on the server-side auth session.
 */
export const ssr = false;
