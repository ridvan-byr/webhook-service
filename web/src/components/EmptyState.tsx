import type { ReactNode } from "react";

/** What a screen shows when there is nothing to list yet: a short invitation, not an apology. */
export function EmptyState({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section aria-label={title} className="border border-line bg-surface px-4 py-8 text-center">
      <h2 className="font-medium">{title}</h2>
      <p className="mt-1 text-muted">{children}</p>
    </section>
  );
}
