import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { StatusFilter } from "@/components/StatusFilter";

describe("StatusFilter", () => {
  it("offers every status and a way back to all of them, as links to the same page", () => {
    render(<StatusFilter current={undefined} />);

    const nav = screen.getByRole("navigation", { name: "Filter by status" });
    const links = Array.from(nav.querySelectorAll("a")).map((a) => [a.textContent, a.getAttribute("href")]);

    expect(links).toEqual([
      ["All", "/deliveries"],
      ["Pending", "/deliveries?status=Pending"],
      ["In progress", "/deliveries?status=InProgress"],
      ["Succeeded", "/deliveries?status=Succeeded"],
      ["Dead", "/deliveries?status=Dead"],
    ]);
  });

  it.each([
    [undefined, "All"],
    ["Dead", "Dead"],
    ["InProgress", "In progress"],
  ])("marks only the current filter (%s)", (current, label) => {
    render(<StatusFilter current={current} />);

    const marked = screen
      .getAllByRole("link")
      .filter((a) => a.getAttribute("aria-current") === "true")
      .map((a) => a.textContent);

    expect(marked).toEqual([label]);
  });

  it("marks All when the filter is something it does not know", () => {
    render(<StatusFilter current="bogus" />);

    expect(screen.getByRole("link", { name: "All" })).toHaveAttribute("aria-current", "true");
  });
});
