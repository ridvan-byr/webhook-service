import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

vi.mock("@/app/(app)/events/new/actions", () => ({ sendEvent: vi.fn() }));

import NewEventPage from "@/app/(app)/events/new/page";

describe("send test event page", () => {
  it("shows the form under one level 1 heading with the name of the screen", () => {
    render(<NewEventPage />);

    expect(screen.getAllByRole("heading", { level: 1 })).toHaveLength(1);
    expect(screen.getByRole("heading", { level: 1, name: "Send test event" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Send event" })).toBeInTheDocument();
  });
});
