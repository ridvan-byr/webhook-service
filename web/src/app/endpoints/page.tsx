import type { Metadata } from "next";
import { EmptyState } from "@/components/EmptyState";
import { PageHeading } from "@/components/PageHeading";

export const metadata: Metadata = { title: "Endpoints" };

export default function EndpointsPage() {
  return (
    <>
      <PageHeading title="Endpoints" description="The addresses that receive your events." />
      <EmptyState title="Not connected yet">
        Signing in and the connection to the API come in the next steps.
      </EmptyState>
    </>
  );
}
