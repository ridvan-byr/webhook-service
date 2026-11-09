import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import axe from "axe-core";
import { describe, expect, it, vi } from "vitest";
import { EndpointsTable } from "@/components/EndpointsTable";
import type { Endpoint } from "@/lib/api/endpoints";

const endpoint = (overrides: Partial<Endpoint> = {}): Endpoint => ({
  id: "e1e1e1e1-0000-4000-8000-000000000002",
  url: "https://shop.example.com/hooks",
  isActive: true,
  createdAt: "2026-01-05T08:00:00+00:00",
  ...overrides,
});

describe("EndpointsTable", () => {
  it("is a table with a name and a header for every column", () => {
    render(<EndpointsTable endpoints={[endpoint()]} setActive={vi.fn()} />);

    const table = screen.getByRole("table", { name: "Endpoints" });

    expect(within(table).getAllByRole("columnheader").map((h) => h.textContent)).toEqual(["Address", "Status", "Added", "Action"]);
  });

  it("shows the address, the state in words and the time in UTC", () => {
    render(<EndpointsTable endpoints={[endpoint(), endpoint({ id: "e2", url: "https://b.example", isActive: false })]} setActive={vi.fn()} />);

    const [active, inactive] = screen.getAllByRole("row").slice(1);

    expect(within(active).getByText("https://shop.example.com/hooks")).toBeInTheDocument();
    expect(within(active).getByText("Active")).toBeInTheDocument();
    expect(within(active).getByText("2026-01-05 08:00:00 UTC")).toBeInTheDocument();
    expect(within(inactive).getByText("Inactive")).toBeInTheDocument();
  });

  it("gives an active and an inactive endpoint different icons as well as different words", () => {
    const { container } = render(
      <EndpointsTable endpoints={[endpoint(), endpoint({ id: "e2", isActive: false })]} setActive={vi.fn()} />,
    );

    const icons = [...container.querySelectorAll("tbody svg")].map((svg) => svg.innerHTML);

    expect(icons).toHaveLength(2);
    expect(icons[0]).not.toBe(icons[1]);
  });

  it("names each button after the endpoint it changes, so they can be told apart", () => {
    render(
      <EndpointsTable
        endpoints={[endpoint(), endpoint({ id: "e2", url: "https://b.example", isActive: false })]}
        setActive={vi.fn()}
      />,
    );

    expect(screen.getByRole("button", { name: "Deactivate https://shop.example.com/hooks" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Activate https://b.example" })).toBeInTheDocument();
  });

  it.each([
    [true, false],
    [false, true],
  ])("asks for an endpoint that is active=%s to become active=%s", async (isActive, expected) => {
    const setActive = vi.fn();
    render(<EndpointsTable endpoints={[endpoint({ isActive })]} setActive={setActive} />);

    await userEvent.click(screen.getByRole("button"));

    expect(setActive).toHaveBeenCalledTimes(1);
    expect(setActive.mock.calls[0].slice(0, 2)).toEqual(["e1e1e1e1-0000-4000-8000-000000000002", expected]);
  });

  it("shows an address written by someone else as text, never as markup", () => {
    const { container } = render(
      <EndpointsTable endpoints={[endpoint({ url: "https://x.example/<img src=x onerror=alert(1)>" })]} setActive={vi.fn()} />,
    );

    expect(container.querySelector("img")).toBeNull();
  });

  it("has no accessibility violations that can be detected without a rendering engine", async () => {
    const { container } = render(
      <EndpointsTable endpoints={[endpoint(), endpoint({ id: "e2", url: "https://b.example", isActive: false })]} setActive={vi.fn()} />,
    );

    const results = await axe.run(container, { rules: { "color-contrast": { enabled: false } } });

    expect(results.violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
  });
});
