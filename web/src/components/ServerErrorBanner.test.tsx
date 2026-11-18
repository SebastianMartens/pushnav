import { fireEvent, render, screen } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import { ServerErrorBanner } from "./ServerErrorBanner";
import type { ServerError } from "@/lib/types";

const lx200: ServerError = {
  server: "lx200",
  port: 4030,
  message: "SkySafari / LX200 clients can't connect: port 4030 is in use by another application.",
};
const stellarium: ServerError = {
  server: "stellarium",
  port: 10001,
  message: "Stellarium can't connect: port 10001 is in use by another application.",
};

describe("ServerErrorBanner", () => {
  it("renders nothing without errors", () => {
    const { container } = render(<ServerErrorBanner errors={[]} />);
    expect(container).toBeEmptyDOMElement();
  });

  it("shows one warning per failed server", () => {
    render(<ServerErrorBanner errors={[lx200, stellarium]} />);
    expect(screen.getByText(/port 4030 is in use/)).toBeInTheDocument();
    expect(screen.getByText(/port 10001 is in use/)).toBeInTheDocument();
  });

  it("dismisses a single warning and keeps the other", () => {
    render(<ServerErrorBanner errors={[lx200, stellarium]} />);
    fireEvent.click(screen.getAllByRole("button", { name: /dismiss/i })[0]);
    expect(screen.queryByText(/port 4030 is in use/)).not.toBeInTheDocument();
    expect(screen.getByText(/port 10001 is in use/)).toBeInTheDocument();
  });
});
