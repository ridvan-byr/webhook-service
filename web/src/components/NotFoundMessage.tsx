import Link from "next/link";

/** What a missing page says. The caller decides what region it sits in. */
export function NotFoundMessage() {
  return (
    <>
      <h1 className="text-2xl font-semibold">Page not found</h1>
      <p className="mt-2 text-muted">There is nothing at this address, or it belongs to another tenant.</p>
      <p className="mt-4">
        <Link href="/deliveries" className="underline underline-offset-2">
          Back to deliveries
        </Link>
      </p>
    </>
  );
}
