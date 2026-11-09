import { redirect } from "next/navigation";
import { AppShell } from "@/components/AppShell";
import { getAccount } from "@/lib/session/current";
import { signOut } from "./actions";

export default async function AppLayout({ children }: LayoutProps<"/">) {
  const state = await getAccount();

  // Cookie missing, damaged, run out, or its key revoked: a layout cannot remove the cookie, so the
  // visitor goes through a route that can.
  if (state.status === "signed-out") redirect("/session/expired");

  if (state.status === "api-down") {
    return (
      <AppShell>
        <p role="alert" className="rounded-md bg-dead-bg px-3 py-2 text-dead-fg">
          The API could not be reached. Your session is still good; reload in a moment.
        </p>
      </AppShell>
    );
  }

  return (
    <AppShell account={state.account} signOut={signOut}>
      {children}
    </AppShell>
  );
}
