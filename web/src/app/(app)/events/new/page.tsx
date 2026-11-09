import type { Metadata } from "next";
import { EmptyState } from "@/components/EmptyState";
import { PageHeading } from "@/components/PageHeading";

export const metadata: Metadata = { title: "Send test event" };

export default function NewEventPage() {
  return (
    <>
      <PageHeading title="Send test event" description="Send an event to your endpoints to see it arrive." />
      <EmptyState title="Not connected yet">
        Signing in and the connection to the API come in the next steps.
      </EmptyState>
    </>
  );
}
