import type { Metadata } from "next";
import { LoginForm } from "./LoginForm";
import { signIn } from "./actions";

export const metadata: Metadata = { title: "Sign in" };

export default async function LoginPage({ searchParams }: PageProps<"/login">) {
  const { reason } = await searchParams;

  return (
    <main id="main" className="flex-1">
      <LoginForm action={signIn} expired={reason === "expired"} />
    </main>
  );
}
