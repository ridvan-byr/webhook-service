import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { ApiUnavailable } from "@/components/ApiUnavailable";
import { EmptyState } from "@/components/EmptyState";
import { EndpointsTable } from "@/components/EndpointsTable";
import { PageHeading } from "@/components/PageHeading";
import { listEndpoints } from "@/lib/api/endpoints";
import { problemFor } from "@/lib/api/problem";
import { requireApi } from "@/lib/session/api-access";
import { AddEndpointForm } from "./AddEndpointForm";
import { addEndpoint, setActive } from "./actions";

export const metadata: Metadata = { title: "Endpoints" };

export default async function EndpointsPage({ searchParams }: PageProps<"/endpoints">) {
  const { toggle } = await searchParams;

  const heading = <PageHeading title="Endpoints" description="The addresses that receive your events." />;

  const api = await requireApi();
  if (!api) return <>{heading}<ApiUnavailable /></>;

  const result = await listEndpoints(api);
  if (!result.ok) {
    if (problemFor(result) === "expired") redirect("/session/expired");
    return <>{heading}<ApiUnavailable /></>;
  }

  return (
    <>
      {heading}

      {toggle === "failed" && (
        <p role="alert" className="mb-4 rounded-md bg-dead-bg px-3 py-2 text-dead-fg">
          The endpoint could not be changed. Reload to see its current state.
        </p>
      )}

      <AddEndpointForm action={addEndpoint} />

      {result.data.length > 0 ? (
        <EndpointsTable endpoints={result.data} setActive={setActive} />
      ) : (
        <EmptyState title="No endpoints yet">Add the address of your receiver above to start receiving events.</EmptyState>
      )}
    </>
  );
}
