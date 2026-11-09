import type { Metadata } from "next";
import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { ApiUnavailable } from "@/components/ApiUnavailable";
import { AutoRefresh } from "@/components/AutoRefresh";
import { DeliveriesTable } from "@/components/DeliveriesTable";
import { EmptyState } from "@/components/EmptyState";
import { PageHeading } from "@/components/PageHeading";
import { getEvent } from "@/lib/api/events";
import { problemFor } from "@/lib/api/problem";
import { formatUtc } from "@/lib/format";
import { requireApi } from "@/lib/session/api-access";

export const metadata: Metadata = { title: "Event" };

const NOTICES: Record<string, string> = {
  ok: "Event accepted. It is queued for every active endpoint.",
  duplicate: "This event was already accepted with the same idempotency key, so nothing new was created.",
};

export default async function EventPage({ params, searchParams }: PageProps<"/events/[id]">) {
  const { id } = await params;
  const { sent } = await searchParams;

  const api = await requireApi();
  if (!api) return <ApiUnavailable />;

  const result = await getEvent(api, id);
  if (!result.ok) {
    const problem = problemFor(result);
    if (problem === "expired") redirect("/session/expired");
    if (problem === "not-found") notFound();
    return <ApiUnavailable />;
  }

  const event = result.data;
  // Only a value this page knows is shown; anything else in the address is ignored, never printed.
  const notice = typeof sent === "string" && Object.hasOwn(NOTICES, sent) ? NOTICES[sent] : undefined;

  return (
    <>
      <p className="mb-2">
        <Link href="/deliveries" className="underline underline-offset-2">
          ← All deliveries
        </Link>
      </p>
      <PageHeading title={event.type} description={`Received ${formatUtc(event.createdAt)}`} />
      <AutoRefresh />

      {notice && (
        <p role="status" className="mb-4 rounded-md border border-line bg-surface px-3 py-2">
          {notice}
        </p>
      )}

      <h2 className="mb-2 font-medium">Payload</h2>
      <pre
        role="region"
        aria-label="Payload"
        className="mb-6 whitespace-pre-wrap break-all border border-line bg-surface p-3 font-mono text-sm"
      >
        {JSON.stringify(event.payload, null, 2)}
      </pre>

      <h2 className="mb-2 font-medium">Deliveries</h2>
      {event.deliveries.length > 0 ? (
        <DeliveriesTable deliveries={event.deliveries} />
      ) : (
        <EmptyState title="No deliveries">
          No active endpoint received this event. Add or activate an endpoint, then send it again.
        </EmptyState>
      )}
    </>
  );
}
