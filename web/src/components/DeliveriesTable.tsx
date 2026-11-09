import Link from "next/link";
import { StatusChip } from "@/components/StatusChip";
import type { Delivery } from "@/lib/api/deliveries";
import { formatUtc } from "@/lib/format";

const HEADERS = ["Event", "Endpoint", "Status", "Attempts", "Created", "Next attempt"];

/** Deliveries as a table. Everything in it came from other systems, so it is only ever shown as text. */
export function DeliveriesTable({ deliveries }: { deliveries: Delivery[] }) {
  return (
    <div className="overflow-x-auto border border-line">
      <table className="w-full min-w-[48rem] border-collapse text-left">
        <caption className="sr-only">Deliveries</caption>
        <thead className="bg-surface">
          <tr>
            {HEADERS.map((header) => (
              <th key={header} scope="col" className="border-b border-line px-3 py-2 font-medium">
                {header}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {deliveries.map((delivery) => (
            <tr key={delivery.id} className="border-b border-line last:border-b-0">
              <td className="px-3 py-2">
                <Link href={`/deliveries/${delivery.id}`} className="font-medium underline underline-offset-2">
                  {delivery.eventType}
                </Link>
              </td>
              <td className="break-all px-3 py-2">{delivery.endpointUrl}</td>
              <td className="px-3 py-2">
                <StatusChip status={delivery.status} />
              </td>
              <td className="px-3 py-2">{delivery.attemptCount}</td>
              <td className="whitespace-nowrap px-3 py-2">{formatUtc(delivery.createdAt)}</td>
              <td className="whitespace-nowrap px-3 py-2">{formatUtc(delivery.nextAttemptAt)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
