import Link from "next/link";

export default function NotFound() {
  return (
    <main id="main" className="mx-auto w-full max-w-md flex-1 px-4 py-16">
      <h1 className="text-2xl font-semibold">Page not found</h1>
      <p className="mt-2 text-muted">There is nothing at this address, or it belongs to another tenant.</p>
      <p className="mt-4">
        <Link href="/deliveries" className="underline underline-offset-2">
          Back to deliveries
        </Link>
      </p>
    </main>
  );
}
