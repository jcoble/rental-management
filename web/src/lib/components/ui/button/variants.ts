import { cn, type WithElementRef } from "$lib/utils.js";
import type { HTMLAnchorAttributes, HTMLButtonAttributes } from "svelte/elements";
import { type VariantProps, tv } from "tailwind-variants";

export const buttonVariants = tv({
	base: "m3-state-layer focus-visible:border-ring focus-visible:ring-ring/45 aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 aria-invalid:border-destructive inline-flex shrink-0 items-center justify-center gap-2 rounded-[var(--m3-shape-full)] text-sm font-medium whitespace-nowrap cursor-pointer outline-none focus-visible:ring-[3px] active:scale-[0.985] disabled:pointer-events-none disabled:opacity-50 aria-disabled:pointer-events-none aria-disabled:opacity-50 [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4",
	variants: {
		variant: {
			default: "bg-primary text-primary-foreground hover:shadow-[var(--m3-elevation-1)]",
			destructive:
				"bg-destructive text-destructive-foreground hover:shadow-[var(--m3-elevation-1)] focus-visible:ring-destructive/20 dark:focus-visible:ring-destructive/40",
			outline:
				"border border-border bg-transparent text-primary hover:border-primary/70 hover:bg-accent/55 hover:text-accent-foreground",
			secondary: "bg-secondary text-secondary-foreground hover:shadow-[var(--m3-elevation-1)]",
			ghost: "text-muted-foreground hover:bg-secondary hover:text-foreground",
			link: "text-primary underline-offset-4 hover:underline",
		},
		size: {
			default: "h-10 px-5 py-2 has-[>svg]:px-4",
			sm: "h-8 gap-1.5 px-4 has-[>svg]:px-3",
			lg: "h-12 px-7 has-[>svg]:px-5",
			icon: "size-9",
			"icon-sm": "size-8",
			"icon-lg": "size-10",
		},
	},
	defaultVariants: {
		variant: "default",
		size: "default",
	},
});

export type ButtonVariant = VariantProps<typeof buttonVariants>["variant"];
export type ButtonSize = VariantProps<typeof buttonVariants>["size"];

export type ButtonProps = WithElementRef<HTMLButtonAttributes> &
	WithElementRef<HTMLAnchorAttributes> & {
		variant?: ButtonVariant;
		size?: ButtonSize;
	};
