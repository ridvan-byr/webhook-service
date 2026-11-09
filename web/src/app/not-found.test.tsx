import { render, screen } from "@testing-library/react";
import axe from "axe-core";
import { describe, expect, it } from "vitest";
import NotFound from "@/app/not-found";

describe("not-found page", () => {
  it("says the page does not exist and offers a way back, instead of showing an empty page", async () => {
    const { container } = render(<NotFound />);

    expect(screen.getAllByRole("heading", { level: 1 })).toHaveLength(1);
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("Page not found");
    expect(screen.getByRole("link", { name: "Back to deliveries" })).toHaveAttribute("href", "/deliveries");
    expect(screen.getByRole("main")).toBeInTheDocument();

    const results = await axe.run(container, { rules: { "color-contrast": { enabled: false } } });
    expect(results.violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
  });
});
