import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { StatusChip } from "@/components/StatusChip";

describe("StatusChip", () => {
  it.each([
    ["Pending", "Pending"],
    ["InProgress", "In progress"],
    ["Succeeded", "Succeeded"],
    ["Dead", "Dead"],
  ])("says %s in words", (status, text) => {
    render(<StatusChip status={status} />);

    // Colour alone is not enough for people who cannot tell the colours apart: the word is always there.
    expect(screen.getByText(text)).toBeInTheDocument();
  });

  it("gives each status an icon of its own as well, hidden from screen readers because the word says it", () => {
    const shapes = ["Pending", "InProgress", "Succeeded", "Dead"].map((status) => {
      const { container, unmount } = render(<StatusChip status={status} />);
      const icon = container.querySelector("svg");
      expect(icon).toHaveAttribute("aria-hidden", "true");
      const shape = icon!.innerHTML;
      unmount();
      return shape;
    });

    expect(new Set(shapes).size).toBe(4);
  });

  it("shows a status it does not know as plain text instead of failing", () => {
    render(<StatusChip status="Quarantined" />);

    expect(screen.getByText("Quarantined")).toBeInTheDocument();
  });

  it("never treats the status as markup", () => {
    const { container } = render(<StatusChip status={'<img src=x onerror="alert(1)">'} />);

    expect(container.querySelector("img")).toBeNull();
  });
});
