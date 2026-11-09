import type { Metadata } from "next";
import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { ApiUnavailable } from "@/components/ApiUnavailable";
import { AttemptsTable } from "@/components/AttemptsTable";
import { AutoRefresh } from "@/components/AutoRefresh";
import { PageHeading } from "@/components/PageHeading";
import { StatusChip } from "@/components/StatusChip";
import { getDelivery } from "@/lib/api/deliveries";
import { problemFor } from "@/lib/api/problem";
import { formatUtc } from "@/lib/format";
import { requireApi } from "@/lib/session/api-access";
import { resend } from "./actions";

export const metadata: Metadata = { title: "Delivery" };

export default async function DeliveryPage({ params, searchParams }: PageProps<"/deliveries/[id]">) {
  const { id } = await params;
  const { resend: outcome } = await searchParams;

  const api = await requireApi();
  if (!api) return <ApiUnavailable />;

  const result = await getDelivery(api, id);
  if (!result.ok) {
    const problem = problemFor(result);
    if (problem === "expired") redirect("/session/expired");
    if (problem === "not-found") notFound();
    return <ApiUnavailable />;
  }

  const { delivery, attempts } = result.data;

  return (
    <>
      <p className="mb-2">
        <Link href="/deliveries" className="underline underline-offset-2">
          ← All deliveries
        </Link>
      </p>
      <PageHeading title={delivery.eventType} />
      <AutoRefresh />

      {outcome === "ok" && (
        <p role="status" className="mb-4 rounded-md border border-line bg-surface px-3 py-2">
          Queued again. The worker will send it shortly.
        </p>
      )}
      {outcome === "failed" && (
        <p role="alert" className="mb-4 rounded-md bg-dead-bg px-3 py-2 text-dead-fg">
          The delivery could not be sent again. It may no longer be dead; reload to see its current state.
        </p>
      )}

      <dl className="mb-6 grid grid-cols-[max-content_1fr] gap-x-6 gap-y-2">
        <dt className="text-muted">Status</dt>
        <dd>
          <StatusChip status={delivery.status} />
        </dd>
        <dt className="text-muted">Endpoint</dt>
        <dd className="break-all">{delivery.endpointUrl}</dd>
        <dt className="text-muted">Attempts</dt>
        <dd>{delivery.attemptCount}</dd>
        <dt className="text-muted">Created</dt>
        <dd>{formatUtc(delivery.createdAt)}</dd>
        <dt className="text-muted">Next attempt</dt>
        <dd>{formatUtc(delivery.nextAttemptAt)}</dd>
        {delivery.lastError && (
          <>
            <dt className="text-muted">Last error</dt>
            <dd className="break-all">{delivery.lastError}</dd>
          </>
        )}
        <dt className="text-muted">Event</dt>
        <dd>
          <Link href={`/events/${delivery.eventId}`} className="underline underline-offset-2">
            Event details
          </Link>
        </dd>
      </dl>

      {delivery.status === "Dead" && (
        <form action={resend.bind(null, delivery.id)} className="mb-6">
          <button type="submit" className="rounded-md bg-accent px-4 py-2 text-on-accent">
            Send again
          </button>
        </form>
      )}

      <h2 className="mb-2 font-medium">Attempts</h2>
      <AttemptsTable attempts={attempts} />
    </>
  );
}
