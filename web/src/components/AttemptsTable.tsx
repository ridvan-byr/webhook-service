import type { Attempt } from "@/lib/api/deliveries";
import { formatDuration, formatUtc } from "@/lib/format";

const HEADERS = ["#", "Started", "Took", "Response"];

/** What happened each time the service tried to deliver. The response is the receiver's, so it is only text. */
export function AttemptsTable({ attempts }: { attempts: Attempt[] }) {
  if (attempts.length === 0) return <p className="text-muted">No attempts yet.</p>;

  return (
    <div className="overflow-x-auto border border-line">
      <table className="w-full min-w-[32rem] border-collapse text-left">
        <caption className="sr-only">Attempts</caption>
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
          {attempts.map((attempt, index) => (
            <tr key={attempt.id} className="border-b border-line align-top last:border-b-0">
              <td className="px-3 py-2">{index + 1}</td>
              <td className="whitespace-nowrap px-3 py-2">{formatUtc(attempt.startedAt)}</td>
              <td className="whitespace-nowrap px-3 py-2">{formatDuration(attempt.durationMs)}</td>
              <td className="px-3 py-2">
                {attempt.responseStatus !== null && <div className="font-medium">{`HTTP ${attempt.responseStatus}`}</div>}
                {attempt.error && <div>{attempt.error}</div>}
                {attempt.responseSnippet && (
                  <code className="mt-1 block whitespace-pre-wrap break-all text-sm text-muted">
                    {attempt.responseSnippet}
                  </code>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
