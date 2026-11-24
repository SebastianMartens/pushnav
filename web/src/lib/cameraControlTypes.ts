// fork: camera-controls (specs/camera-server/SPEC_CAMERA_CONTROLS.md sections 6.1, 6.2).
// The protocol v2 control descriptor and stream state. They live here, not in types.ts, so
// the upstream types stay as they are: `ControlDescriptor` there describes the v1 payload.

export interface ControlOption {
  value: number;
  label: string;
}

/**
 * A CONTROL_INFO entry of a v2 session (SPEC_PROTOCOL_CAMERA.md 11.4.1). An enum has `options`
 * and no `min` / `max` / `step`; `type` and `scale` are the protocol's own words
 * ("int" | "float" | "enum", "linear" | "log"), kept as plain strings for ids we do not know.
 * The /ws payload types `controls` as the v1 `ControlDescriptor`, which is assignable to this
 * (a v2 enum just has no range).
 */
export interface ControlDescriptorV2 {
  id?: string;
  name?: string;
  label: string;
  type?: string;
  unit?: string;
  min?: number;
  max?: number;
  step?: number;
  cur: number;
  value?: number;
  scale?: string;
  restarts_stream?: boolean;
  default?: number;
  options?: ControlOption[];
}

/** `camera.stream` of the /ws state: the frames now arriving (STREAM_INFO). */
export interface CameraStream {
  width: number;
  height: number;
  binning: number;
  /** "streaming" or "reconfiguring"; a newer server may send another state. */
  state: string;
  frame_interval_us: number | null;
}

export const STREAM_RECONFIGURING = "reconfiguring";
