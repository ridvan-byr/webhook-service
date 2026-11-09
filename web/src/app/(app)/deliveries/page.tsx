import type { Metadata } from "next";
import { EmptyState } from "@/components/EmptyState";
import { PageHeading } from "@/components/PageHeading";

export const metadata: Metadata = { title: "Deliveries" };

export default function DeliveriesPage() {
  return (
    <>
      <PageHeading title="Deliveries" description="Every delivery of your events, newest first." />
      <EmptyState title="Not connected yet">
        Signing in and the connection to the API come in the next steps.
      </EmptyState>
    </>
  );
}
