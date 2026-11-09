import type { ReactNode } from "react";
import { NavLink } from "@/components/NavLink";

const SCREENS = [
  { href: "/deliveries", label: "Deliveries" },
  { href: "/endpoints", label: "Endpoints" },
  { href: "/events/new", label: "Send test event" },
] as const;

/** The frame around every page: a way to skip the menu, the menu, and the main area. */
export function AppShell({ children }: { children: ReactNode }) {
  return (
    <>
      <a
        href="#main"
        className="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-10 focus:rounded-md focus:bg-accent focus:px-3 focus:py-2 focus:text-on-accent"
      >
        Skip to main content
      </a>

      <header className="border-b border-line bg-surface">
        <nav aria-label="Main" className="mx-auto flex max-w-6xl flex-wrap items-center gap-x-5 px-4">
          <span className="py-3 pr-2 font-medium">Webhook service</span>
          {SCREENS.map((screen) => (
            <NavLink key={screen.href} href={screen.href}>
              {screen.label}
            </NavLink>
          ))}
        </nav>
      </header>

      <main id="main" tabIndex={-1} className="mx-auto w-full max-w-6xl flex-1 px-4 py-6 outline-none">
        {children}
      </main>
    </>
  );
}
