import type { Metadata } from "next";
import { AppShell } from "@/components/AppShell";
import "./globals.css";

export const metadata: Metadata = {
  title: { default: "Webhook service", template: "%s · Webhook service" },
  description: "Admin panel of the webhook delivery service.",
};

// Every page is rendered again for each request, never prepared in advance. The Content-Security-Policy
// carries a nonce that is new for every request, and what a page shows belongs to one signed-in tenant,
// so nothing may be reused between visitors.
export const dynamic = "force-dynamic";

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    <html lang="en">
      <body>
        <AppShell>{children}</AppShell>
      </body>
    </html>
  );
}
