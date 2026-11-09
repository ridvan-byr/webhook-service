import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import DeliveriesPage from "@/app/(app)/deliveries/page";
import EndpointsPage from "@/app/(app)/endpoints/page";
import NewEventPage from "@/app/(app)/events/new/page";

describe("the screens of the menu", () => {
  it.each([
    ["Deliveries", DeliveriesPage],
    ["Endpoints", EndpointsPage],
    ["Send test event", NewEventPage],
  ])("%s has one level 1 heading with its name", (name, Page) => {
    render(<Page />);

    expect(screen.getAllByRole("heading", { level: 1 })).toHaveLength(1);
    expect(screen.getByRole("heading", { level: 1, name })).toBeInTheDocument();
  });
});
