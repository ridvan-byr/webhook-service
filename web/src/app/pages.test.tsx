import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import EndpointsPage from "@/app/(app)/endpoints/page";

// The other screens fetch data or take input, and have their own tests next to them.
describe("the screens that are not built yet", () => {
  it("Endpoints has one level 1 heading with its name", () => {
    render(<EndpointsPage />);

    expect(screen.getAllByRole("heading", { level: 1 })).toHaveLength(1);
    expect(screen.getByRole("heading", { level: 1, name: "Endpoints" })).toBeInTheDocument();
  });
});
