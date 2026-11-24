import { useState } from "react";
import { Slider } from "@/components/ui/slider";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { api } from "@/lib/api";
import {
  STREAM_RECONFIGURING,
  type CameraStream,
  type ControlDescriptorV2,
} from "@/lib/cameraControlTypes";
import {
  controlId,
  formatControlValue,
  serverValueOf,
  sliderBounds,
  sortControls,
  toSliderPosition,
  valueAtPosition,
} from "@/lib/cameraControls";

interface Props {
  controls: ControlDescriptorV2[];
  stream: CameraStream;
}

// fork: camera-controls (spec 6.2). The controls of a protocol v2 camera, which App.tsx renders
// when `camera.stream` is present; a v1 camera keeps the upstream CameraControls. Each
// control by its type: int / float as a slider (log10 for `scale: "log"`), enum as buttons
// (2 to 4 options) or a select (more). While the camera reconfigures its stream (binning)
// the controls are disabled.
export function CameraControlsV2({ controls, stream }: Props) {
  const reconfiguring = stream.state === STREAM_RECONFIGURING;
  return (
    <Card>
      <CardHeader>
        <CardTitle>Camera</CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        {reconfiguring && (
          <p role="status" className="text-sm text-muted-foreground">
            Reconfiguring camera…
          </p>
        )}
        {sortControls(controls).map((c) => (
          <ControlRow key={controlId(c)} control={c} disabled={reconfiguring} />
        ))}
      </CardContent>
    </Card>
  );
}

function ControlRow({ control, disabled }: { control: ControlDescriptorV2; disabled: boolean }) {
  return control.type === "enum" ? (
    <EnumRow control={control} disabled={disabled} />
  ) : (
    <SliderRow control={control} disabled={disabled} />
  );
}

function SliderRow({ control, disabled }: { control: ControlDescriptorV2; disabled: boolean }) {
  const id = controlId(control);
  const serverValue = serverValueOf(control);
  // The slider position (log10(value) on a log control) is what the user moves; the value
  // shown is that position snapped to the camera's step.
  const [position, setPosition] = useState(() => toSliderPosition(control, serverValue));

  // Reflect server-side updates (adjusting state while rendering, not in an effect)
  const [seenServerValue, setSeenServerValue] = useState(serverValue);
  if (seenServerValue !== serverValue) {
    setSeenServerValue(serverValue);
    setPosition(toSliderPosition(control, serverValue));
  }

  const commit = (p: number) => {
    setPosition(p);
    api.setControl(id, valueAtPosition(control, p)).catch((e) => {
      console.error(e);
      setPosition(toSliderPosition(control, serverValue)); // the camera never took it: show what it has
    });
  };

  const { min, max, step } = sliderBounds(control);
  return (
    <div>
      <div className="flex justify-between text-sm mb-1">
        <span>{control.label}</span>
        <span className="text-muted-foreground">
          {formatControlValue(valueAtPosition(control, position), control.unit, control.step)}
        </span>
      </div>
      <Slider
        min={min}
        max={max}
        step={step}
        value={[position]}
        disabled={disabled}
        onValueChange={([p]) => setPosition(p)}
        onValueCommit={([p]) => commit(p)}
      />
    </div>
  );
}

// More options than this turn the buttons into a select (they would not fit the narrow column).
const MAX_BUTTON_OPTIONS = 4;

function EnumRow({ control, disabled }: { control: ControlDescriptorV2; disabled: boolean }) {
  const id = controlId(control);
  const options = control.options ?? [];
  const serverValue = serverValueOf(control);
  const [local, setLocal] = useState(serverValue);
  const [seenServerValue, setSeenServerValue] = useState(serverValue);
  if (seenServerValue !== serverValue) {  // reflect server-side updates
    setSeenServerValue(serverValue);
    setLocal(serverValue);
  }

  const commit = (v: number) => {
    setLocal(v);
    api.setControl(id, v).catch((e) => {
      console.error(e);
      setLocal(serverValue); // the camera never took it: show what it has
    });
  };

  return (
    <div>
      <div className="text-sm mb-1">{control.label}</div>
      {options.length <= MAX_BUTTON_OPTIONS ? (
        <div role="group" aria-label={control.label} className="flex gap-1">
          {options.map((o) => (
            <Button
              key={o.value}
              size="sm"
              variant={o.value === local ? "default" : "outline"}
              aria-pressed={o.value === local}
              disabled={disabled}
              className="flex-1"
              onClick={() => commit(o.value)}
            >
              {o.label}
            </Button>
          ))}
        </div>
      ) : (
        <select
          aria-label={control.label}
          value={local}
          disabled={disabled}
          onChange={(e) => commit(Number(e.target.value))}
          className="h-8 w-full rounded-lg border border-border bg-background px-2 text-sm disabled:opacity-50"
        >
          {options.map((o) => (
            <option key={o.value} value={o.value}>{o.label}</option>
          ))}
        </select>
      )}
    </div>
  );
}
