import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import EndpointsPage from "@/app/(app)/endpoints/page";
import NewEventPage from "@/app/(app)/events/new/page";

// The deliveries pages fetch data and have their own tests next to them.
describe("the screens that are not built yet", () => {
  it.each([
    ["Endpoints", EndpointsPage],
    ["Send test event", NewEventPage],
  ])("%s has one level 1 heading with its name", (name, Page) => {
    render(<Page />);

    expect(screen.getAllByRole("heading", { level: 1 })).toHaveLength(1);
    expect(screen.getByRole("heading", { level: 1, name })).toBeInTheDocument();
  });
});
