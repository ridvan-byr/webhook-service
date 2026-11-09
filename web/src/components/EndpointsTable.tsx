import type { Endpoint } from "@/lib/api/endpoints";
import { formatUtc } from "@/lib/format";

// The state and its button come before the date, so both are on screen on a phone, where the table scrolls sideways.
const HEADERS = ["Address", "Status", "Action", "Added"];
const STROKE = { fill: "none", stroke: "currentColor", strokeWidth: 2, strokeLinecap: "round", strokeLinejoin: "round" } as const;

function ActiveChip({ isActive }: { isActive: boolean }) {
  // Colour, icon and word together, like the delivery states.
  return (
    <span
      className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-0.5 text-sm ${
        isActive ? "bg-ok-bg text-ok-fg" : "border border-line bg-surface text-fg"
      }`}
    >
      <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true" focusable="false">
        {isActive ? <path d="M3 8.5l3.5 3.5L13 4.5" {...STROKE} /> : <path d="M5.5 3.5v9M10.5 3.5v9" {...STROKE} />}
      </svg>
      {isActive ? "Active" : "Inactive"}
    </span>
  );
}

/** The endpoints of the tenant, each with a button that switches it on or off. */
export function EndpointsTable({
  endpoints,
  setActive,
}: {
  endpoints: Endpoint[];
  setActive: (id: string, isActive: boolean, formData?: FormData) => void | Promise<void>;
}) {
  return (
    <div className="overflow-x-auto border border-line">
      <table className="w-full min-w-[40rem] border-collapse text-left">
        <caption className="sr-only">Endpoints</caption>
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
          {endpoints.map((endpoint) => (
            <tr key={endpoint.id} className="border-b border-line last:border-b-0">
              <td className="min-w-[12rem] break-all px-3 py-2">{endpoint.url}</td>
              <td className="px-3 py-2">
                <ActiveChip isActive={endpoint.isActive} />
              </td>
              <td className="px-3 py-2">
                <form action={setActive.bind(null, endpoint.id, !endpoint.isActive)}>
                  <button
                    type="submit"
                    aria-label={`${endpoint.isActive ? "Deactivate" : "Activate"} ${endpoint.url}`}
                    className="rounded-md border border-line px-3 py-1.5 hover:bg-page"
                  >
                    {endpoint.isActive ? "Deactivate" : "Activate"}
                  </button>
                </form>
              </td>
              <td className="whitespace-nowrap px-3 py-2">{formatUtc(endpoint.createdAt)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
