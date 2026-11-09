import type { Metadata } from "next";
import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { ApiUnavailable } from "@/components/ApiUnavailable";
import { AutoRefresh } from "@/components/AutoRefresh";
import { DeliveriesTable } from "@/components/DeliveriesTable";
import { EmptyState } from "@/components/EmptyState";
import { PageHeading } from "@/components/PageHeading";
import { StatusFilter } from "@/components/StatusFilter";
import { listDeliveries } from "@/lib/api/deliveries";
import { problemFor } from "@/lib/api/problem";
import { requireApi } from "@/lib/session/api-access";

export const metadata: Metadata = { title: "Deliveries" };

const WORDS: Record<string, string> = { Pending: "pending", InProgress: "in progress", Succeeded: "succeeded", Dead: "dead" };

export default async function DeliveriesPage({ searchParams }: PageProps<"/deliveries">) {
  const { status: rawStatus } = await searchParams;
  const status = typeof rawStatus === "string" ? rawStatus : undefined;

  const heading = <PageHeading title="Deliveries" description="Every delivery of your events, newest first." />;

  const api = await requireApi();
  if (!api) return <>{heading}<ApiUnavailable /></>;

  const result = await listDeliveries(api, status);
  if (!result.ok) {
    const problem = problemFor(result);
    if (problem === "expired") redirect("/session/expired");
    if (problem === "not-found") notFound();
    return <>{heading}<ApiUnavailable /></>;
  }

  const word = status ? WORDS[status] : undefined;

  return (
    <>
      {heading}
      <AutoRefresh />
      <StatusFilter current={status} />
      {result.data.length > 0 ? (
        <DeliveriesTable deliveries={result.data} />
      ) : word ? (
        <EmptyState title={`No ${word} deliveries`}>Nothing is {word} right now.</EmptyState>
      ) : (
        <EmptyState title="No deliveries yet">
          Deliveries appear here once an event is sent to an endpoint.{" "}
          <Link href="/events/new" className="underline underline-offset-2">
            Send a test event
          </Link>
          .
        </EmptyState>
      )}
    </>
  );
}
