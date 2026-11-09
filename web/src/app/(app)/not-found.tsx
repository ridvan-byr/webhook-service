import { NotFoundMessage } from "@/components/NotFoundMessage";

// Shown when a signed-in screen finds nothing (an unknown delivery or event). It sits inside the app
// shell, which already has the main region, so it must not add another.
export default function AppNotFound() {
  return (
    <div className="max-w-md py-10">
      <NotFoundMessage />
    </div>
  );
}
