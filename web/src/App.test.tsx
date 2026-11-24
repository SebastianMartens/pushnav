import { render, screen } from "@testing-library/react";
import { beforeEach, describe, it, expect, vi } from "vitest";
import type { EnginePayload } from "@/lib/types";

// fork: camera-controls. Which controls card App picks: the v2 one for a protocol v2 session
// (`camera.protocol_version === 2`), the upstream one otherwise. Everything else in App is stubbed.
let mockState: EnginePayload | null = null; // read lazily by the mocked hook

vi.mock("@/hooks/useEngineState", () => ({ useEngineState: () => mockState }));
vi.mock("@/hooks/useView", () => ({ useView: () => ["navigation", () => {}] }));
vi.mock("@/components/controls/CameraControls", () => ({
  CameraControls: () => <div data-testid="controls-v1" />,
}));
vi.mock("@/components/controls/CameraControlsV2", () => ({
  CameraControlsV2: (props: { stream?: unknown }) => (
    <div data-testid="controls-v2" data-stream={props.stream === null ? "null" : String(props.stream !== undefined)} />
  ),
}));
vi.mock("@/components/live-view/LiveView", () => ({ LiveView: () => null }));
vi.mock("@/components/wizard/Wizard", () => ({ Wizard: () => null }));
vi.mock("@/components/settings/Settings", () => ({ Settings: () => null }));
vi.mock("@/components/settings/Connectivity", () => ({ Connectivity: () => null }));
vi.mock("@/components/splash/Splash", () => ({ Splash: () => null }));
vi.mock("@/components/StateHeader", () => ({ StateHeader: () => null }));
vi.mock("@/components/StepIndicator", () => ({ StepIndicator: () => null }));
vi.mock("@/components/debug/DebugPanel", () => ({ DebugPanel: () => null }));
vi.mock("@/components/catalog/WhatToSee", () => ({ WhatToSee: () => null }));
vi.mock("@/components/ServerErrorBanner", () => ({ ServerErrorBanner: () => null }));

import App from "./App";

function makeState(camera: Partial<EnginePayload["camera"]>): EnginePayload {
  return {
    controls: [], dev_mode: false, server_errors: [],
    camera: { connected: true, all_centroids: null, matched_centroids: null, ...camera },
  } as unknown as EnginePayload;
}

const stream = { width: 968, height: 608, binning: 2, state: "streaming", frame_interval_us: 250000 };

beforeEach(() => {
  mockState = null;
});

describe("App picks the controls card by protocol version", () => {
  it("renders the upstream card for a v1 session", () => {
    mockState = makeState({ protocol_version: 1, stream: null });
    render(<App />);
    expect(screen.getByTestId("controls-v1")).toBeInTheDocument();
    expect(screen.queryByTestId("controls-v2")).not.toBeInTheDocument();
  });

  it("renders the upstream card for an engine that sends no protocol_version", () => {
    mockState = makeState({});
    render(<App />);
    expect(screen.getByTestId("controls-v1")).toBeInTheDocument();
  });

  it("renders the v2 card for a v2 session, with its stream", () => {
    mockState = makeState({ protocol_version: 2, stream });
    render(<App />);
    expect(screen.getByTestId("controls-v2")).toHaveAttribute("data-stream", "true");
    expect(screen.queryByTestId("controls-v1")).not.toBeInTheDocument();
  });

  it("keeps the v2 card while a v2 client has no stream (disconnected, or before the first STREAM_INFO)", () => {
    mockState = makeState({ protocol_version: 2, stream: null, connected: false });
    render(<App />);
    expect(screen.getByTestId("controls-v2")).toHaveAttribute("data-stream", "null");
    expect(screen.queryByTestId("controls-v1")).not.toBeInTheDocument();
  });

  it("does not choose the v2 card from a stream alone", () => {
    mockState = makeState({ protocol_version: 1, stream });
    render(<App />);
    expect(screen.getByTestId("controls-v1")).toBeInTheDocument();
  });
});
