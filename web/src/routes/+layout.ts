// SSR is enabled so hooks.server.ts can populate locals.user and the root
// layout can seed the client auth store before hydration.
export const ssr = true;
export const prerender = false;
