import { render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { describe, expect, it } from "vitest";
import { DeliveriesTable } from "@/components/DeliveriesTable";
import type { Delivery } from "@/lib/api/deliveries";

const delivery = (overrides: Partial<Delivery> = {}): Delivery => ({
  id: "3f2a91c0-1b2d-4e5f-8a9b-0c1d2e3f4a5b",
  eventId: "c71e04b9-0000-4000-8000-000000000001",
  eventType: "order.created",
  endpointId: "e1e1e1e1-0000-4000-8000-000000000002",
  endpointUrl: "https://shop.example.com/hooks",
  status: "Dead",
  attemptCount: 8,
  nextAttemptAt: "2026-01-05T09:00:00+00:00",
  lastError: "HTTP 500",
  createdAt: "2026-01-05T08:00:00+00:00",
  ...overrides,
});

describe("DeliveriesTable", () => {
  it("is a table with a name and a header for every column", () => {
    render(<DeliveriesTable deliveries={[delivery()]} />);

    const table = screen.getByRole("table", { name: "Deliveries" });
    const headers = within(table).getAllByRole("columnheader").map((h) => h.textContent);

    // Status comes second so that it is still on screen on a phone, where the table scrolls sideways.
    expect(headers).toEqual(["Event", "Status", "Endpoint", "Attempts", "Created", "Next attempt"]);
  });

  it("shows what a delivery is about in one row, with times in UTC", () => {
    render(<DeliveriesTable deliveries={[delivery()]} />);

    const row = screen.getAllByRole("row")[1];

    expect(within(row).getByText("https://shop.example.com/hooks")).toBeInTheDocument();
    expect(within(row).getByText("Dead")).toBeInTheDocument();
    expect(within(row).getByText("8")).toBeInTheDocument();
    expect(within(row).getByText("2026-01-05 08:00:00 UTC")).toBeInTheDocument();
    expect(within(row).getByText("2026-01-05 09:00:00 UTC")).toBeInTheDocument();
  });

  it("links the event type to the page of that delivery", () => {
    render(<DeliveriesTable deliveries={[delivery()]} />);

    expect(screen.getByRole("link", { name: "order.created" })).toHaveAttribute(
      "href",
      "/deliveries/3f2a91c0-1b2d-4e5f-8a9b-0c1d2e3f4a5b",
    );
  });

  it("keeps the order it was given", () => {
    render(
      <DeliveriesTable
        deliveries={[
          delivery({ id: "00000000-0000-4000-8000-00000000000a", eventType: "first" }),
          delivery({ id: "00000000-0000-4000-8000-00000000000b", eventType: "second" }),
        ]}
      />,
    );

    expect(screen.getAllByRole("link").map((l) => l.textContent)).toEqual(["first", "second"]);
  });

  it("shows text written by other systems as text, never as markup", () => {
    const { container } = render(
      <DeliveriesTable
        deliveries={[
          delivery({
            eventType: "<img src=x onerror=alert(1)>",
            endpointUrl: "https://x.example/<script>alert(1)</script>",
          }),
        ]}
      />,
    );

    expect(container.querySelector("img, script")).toBeNull();
    expect(screen.getByText("<img src=x onerror=alert(1)>")).toBeInTheDocument();
  });

  it("has no accessibility violations that can be detected without a rendering engine", async () => {
    const { container } = render(<DeliveriesTable deliveries={[delivery(), delivery({ status: "Pending" })]} />);

    const results = await axe.run(container, { rules: { "color-contrast": { enabled: false } } });

    expect(results.violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
  });
});
