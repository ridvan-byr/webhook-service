import type { ReactNode } from "react";

interface Look {
  label: string;
  className: string;
  icon: ReactNode;
}

const STROKE = { fill: "none", stroke: "currentColor", strokeWidth: 2, strokeLinecap: "round", strokeLinejoin: "round" } as const;

// Each status has its own colour, its own icon and its own word, so no one of them has to carry the meaning.
const LOOKS: Record<string, Look> = {
  Pending: {
    label: "Pending",
    className: "bg-pending-bg text-pending-fg",
    icon: (
      <>
        <circle cx="8" cy="8" r="6" {...STROKE} />
        <path d="M8 4.5V8l2.5 1.5" {...STROKE} />
      </>
    ),
  },
  InProgress: {
    label: "In progress",
    className: "bg-progress-bg text-progress-fg",
    icon: (
      <>
        <path d="M13 8a5 5 0 1 1-1.5-3.5" {...STROKE} />
        <path d="M13 2.5v3h-3" {...STROKE} />
      </>
    ),
  },
  Succeeded: {
    label: "Succeeded",
    className: "bg-ok-bg text-ok-fg",
    icon: <path d="M3 8.5l3.5 3.5L13 4.5" {...STROKE} />,
  },
  Dead: {
    label: "Dead",
    className: "bg-dead-bg text-dead-fg",
    icon: <path d="M4 4l8 8M12 4l-8 8" {...STROKE} />,
  },
};

const UNKNOWN_CLASS = "bg-surface text-fg border border-line";

/** The state of a delivery: colour, icon and text together. An unknown state is shown as the text it came with. */
export function StatusChip({ status }: { status: string }) {
  const look = LOOKS[status];

  return (
    <span
      className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-0.5 text-sm ${look?.className ?? UNKNOWN_CLASS}`}
    >
      {look && (
        <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true" focusable="false">
          {look.icon}
        </svg>
      )}
      {look?.label ?? status}
    </span>
  );
}
