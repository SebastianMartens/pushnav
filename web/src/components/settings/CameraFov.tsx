import { useState } from "react";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { api } from "@/lib/api";
import type { EnginePayload, FovSource } from "@/lib/types";

// fork: camera-controls (see specs/camera-server/SPEC_CAMERA_CONTROLS.md section 6.3).
// Camera, field of view (FOV) with its source and the solver database, focal length, reset.

interface Props {
  state: EnginePayload;
}

const SOURCE_LABEL: Record<FovSource, string> = {
  user: "set by you",
  learned: "learned from the sky",
  searching: "searching",
  default: "default",
};

function fovText(state: Pick<EnginePayload, "fov_h_deg" | "fov_source">): string {
  if (state.fov_source === "searching") return "Finding field of view…";
  return `${state.fov_h_deg.toFixed(2)}° (${SOURCE_LABEL[state.fov_source] ?? state.fov_source})`;
}

export function CameraFov({ state }: Props) {
  const [error, setError] = useState<string | null>(null);
  // What the user is typing; null = show the engine's value. Kept while the engine's value changes
  // under the user's hands (searching -> learned fills in the focal length).
  const [draft, setDraft] = useState<string | null>(null);
  const cam = state.camera;
  const connected = cam.camera_key != null || cam.camera_model != null;
  const sensorKnown = cam.sensor_width_mm != null;
  const canEnterLens = connected && sensorKnown;

  function run(body: Parameters<typeof api.setCameraFov>[0]) {
    setError(null);
    api.setCameraFov(body).catch((e: Error) => setError(e.message.replace(/^POST \S+ → \d+: /, "")));
  }

  function commitFocalLength(raw: string) {
    const value = Number(raw);
    if (raw.trim() === "" || !Number.isFinite(value)) return; // empty: nothing to send
    // The pre-filled value of a learned FOV is the user's to confirm: entering it makes it a user value.
    if (value === cam.focal_length_mm && state.fov_source === "user") return; // unchanged
    run({ focal_length_mm: value });
  }

  return (
    <div className="space-y-2" data-testid="camera-fov">
      <div className="text-sm font-medium text-primary">Camera</div>
      <Row label="Model">
        <span className="text-sm text-right" data-testid="camera-name">
          {connected
            ? [cam.camera_model, cam.backend && `(${cam.backend})`].filter(Boolean).join(" ")
            : "not connected"}
        </span>
      </Row>
      <Row label="Field of view">
        <span className="text-sm text-right" data-testid="fov-text">{fovText(state)}</span>
      </Row>
      <Row label="Star database">
        <span className="text-sm" data-testid="solver-db">{state.solver_db ?? "not loaded"}</span>
      </Row>
      <Row label="Focal length (mm)">
        <Input
          type="number"
          min={1}
          step={0.5}
          placeholder="e.g. 25"
          disabled={!canEnterLens}
          value={draft ?? cam.focal_length_mm ?? ""}
          className="w-24 h-8"
          aria-label="Focal length in millimetres"
          onChange={(e) => setDraft(e.currentTarget.value)}
          onBlur={(e) => {
            commitFocalLength(e.currentTarget.value);
            setDraft(null);
          }}
          onKeyDown={(e) => {
            if (e.key === "Enter") e.currentTarget.blur();
          }}
        />
      </Row>
      <p className="text-xs text-muted-foreground leading-snug">
        {canEnterLens
          ? "Skips the field-of-view search: the lens and the sensor size give the field of view."
          : connected
            ? "This camera does not report its sensor size, so a focal length cannot be converted."
            : "Connect a camera to set its focal length."}
      </p>
      {state.fov_source === "user" && (
        <Button
          variant="outline"
          size="sm"
          className="w-full"
          onClick={() => run({ reset: true })}
        >
          Reset to learned
        </Button>
      )}
      {state.fov_out_of_range && (
        <p className="text-xs text-destructive leading-snug" role="alert">
          No star database covers this field of view.
        </p>
      )}
      {error && (
        <p className="text-xs text-destructive leading-snug" role="alert">
          {error}
        </p>
      )}
    </div>
  );
}

function Row({
  label,
  children,
}: {
  label: React.ReactNode;
  children: React.ReactNode;
}) {
  return (
    <div className="flex items-center justify-between gap-2">
      <span className="text-sm">{label}</span>
      {children}
    </div>
  );
}
