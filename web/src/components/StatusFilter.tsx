import Link from "next/link";

const FILTERS = [
  { status: undefined, label: "All" },
  { status: "Pending", label: "Pending" },
  { status: "InProgress", label: "In progress" },
  { status: "Succeeded", label: "Succeeded" },
  { status: "Dead", label: "Dead" },
] as const;

/** Narrows the list of deliveries to one status. Plain links, so it works with the address bar and back button. */
export function StatusFilter({ current }: { current: string | undefined }) {
  const active = FILTERS.some((f) => f.status === current) ? current : undefined;

  return (
    <nav aria-label="Filter by status" className="mb-4 flex flex-wrap gap-2">
      {FILTERS.map(({ status, label }) => {
        const isCurrent = status === active;
        return (
          <Link
            key={label}
            href={status ? `/deliveries?status=${status}` : "/deliveries"}
            aria-current={isCurrent ? "true" : undefined}
            className={`rounded-full border px-3 py-1 text-sm ${
              isCurrent ? "border-accent bg-accent text-on-accent" : "border-line bg-surface hover:bg-page"
            }`}
          >
            {label}
          </Link>
        );
      })}
    </nav>
  );
}
