"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import type { ReactNode } from "react";

/** A menu link that tells assistive technology (and shows) which page you are on. */
export function NavLink({ href, children }: { href: string; children: ReactNode }) {
  const pathname = usePathname();
  const current = pathname === href || pathname.startsWith(`${href}/`);

  return (
    <Link
      href={href}
      aria-current={current ? "page" : undefined}
      className={
        current
          ? "border-b-2 border-accent py-3 font-medium text-fg"
          : "border-b-2 border-transparent py-3 text-muted hover:text-fg"
      }
    >
      {children}
    </Link>
  );
}
