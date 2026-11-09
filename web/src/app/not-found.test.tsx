import { render, screen } from "@testing-library/react";
import axe from "axe-core";
import { describe, expect, it } from "vitest";
import AppNotFound from "@/app/(app)/not-found";
import RootNotFound from "@/app/not-found";

describe("not-found pages", () => {
  it("outside the signed-in screens, it is the whole page: it has its own main region", async () => {
    const { container } = render(<RootNotFound />);

    expect(screen.getAllByRole("heading", { level: 1 })).toHaveLength(1);
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("Page not found");
    expect(screen.getByRole("link", { name: "Back to deliveries" })).toHaveAttribute("href", "/deliveries");
    expect(screen.getAllByRole("main")).toHaveLength(1);

    const results = await axe.run(container, { rules: { "color-contrast": { enabled: false } } });
    expect(results.violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
  });

  it("inside the signed-in screens, it goes into the main region that is already there instead of adding a second one", () => {
    const { container } = render(<AppNotFound />);

    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("Page not found");
    expect(screen.getByRole("link", { name: "Back to deliveries" })).toHaveAttribute("href", "/deliveries");
    expect(container.querySelector("main, #main")).toBeNull();
  });
});
