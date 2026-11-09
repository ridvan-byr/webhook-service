import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import axe from "axe-core";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { AppShell } from "@/components/AppShell";

const pathname = vi.hoisted(() => ({ current: "/deliveries" }));
vi.mock("next/navigation", () => ({ usePathname: () => pathname.current }));

function renderShell() {
  return render(
    <AppShell>
      <h1>Page title</h1>
    </AppShell>,
  );
}

beforeEach(() => {
  pathname.current = "/deliveries";
});

describe("AppShell", () => {
  it("offers a main navigation with a link to every screen", () => {
    renderShell();

    const nav = screen.getByRole("navigation", { name: "Main" });
    const names = Array.from(nav.querySelectorAll("a")).map((a) => a.textContent);

    expect(names).toEqual(["Deliveries", "Endpoints", "Send test event"]);
  });

  it.each([
    ["/deliveries", "Deliveries"],
    ["/deliveries/3f2a91c0", "Deliveries"],
    ["/endpoints", "Endpoints"],
    ["/events/new", "Send test event"],
  ])("marks only the link of the current page (%s)", (path, current) => {
    pathname.current = path;
    renderShell();

    const marked = screen
      .getAllByRole("link")
      .filter((link) => link.getAttribute("aria-current") === "page")
      .map((link) => link.textContent);

    expect(marked).toEqual([current]);
  });

  it("marks no link on a page that is not in the menu", () => {
    pathname.current = "/events/c71e04b9";
    renderShell();

    expect(screen.queryByRole("link", { current: "page" })).toBeNull();
  });

  it("has exactly one main region, where the page content goes", () => {
    renderShell();

    const main = screen.getByRole("main");

    expect(main).toHaveAttribute("id", "main");
    expect(main).toContainElement(screen.getByRole("heading", { name: "Page title" }));
  });

  it("starts with a link that skips the menu, and it is the first thing the keyboard reaches", async () => {
    renderShell();

    await userEvent.tab();

    const skip = screen.getByRole("link", { name: "Skip to main content" });
    expect(skip).toHaveFocus();
    expect(skip).toHaveAttribute("href", "#main");
  });

  it("has no accessibility violations that can be detected without a rendering engine", async () => {
    const { container } = renderShell();

    // jsdom does not lay anything out, so the contrast rule cannot be evaluated here.
    // Contrast is checked on the palette itself and, later, in a real browser.
    const results = await axe.run(container, { rules: { "color-contrast": { enabled: false } } });

    expect(results.violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
  });
});
