import { NotFoundMessage } from "@/components/NotFoundMessage";

// Shown for an address that no screen owns. It is the whole page, so it brings its own main region.
export default function NotFound() {
  return (
    <main id="main" className="mx-auto w-full max-w-md flex-1 px-4 py-16">
      <NotFoundMessage />
    </main>
  );
}
