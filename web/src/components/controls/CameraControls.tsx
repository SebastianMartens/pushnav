import { useState } from "react";
import { Slider } from "@/components/ui/slider";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { api } from "@/lib/api";
import {
  controlId,
  formatControlValue,
  fromSliderPosition,
  isLogScale,
  sliderBounds,
  snapToStep,
  sortControls,
  toSliderPosition,
} from "@/lib/controlFormat";
import type { CameraStream, ControlDescriptor } from "@/lib/types";

interface Props {
  controls: ControlDescriptor[];
  /** `camera.stream` of the /ws state; null / absent for a v1 camera. */
  stream?: CameraStream | null;
}

// fork: camera-controls (spec 6.2). Each advertised control by its type: int / float as a
// slider (log10 for `scale: "log"`), enum as buttons (2 to 4 options) or a select (more).
// While the camera reconfigures its stream (binning) the controls are disabled.
export function CameraControls({ controls, stream }: Props) {
  const reconfiguring = stream?.state === "reconfiguring";
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

function ControlRow({ control, disabled }: { control: ControlDescriptor; disabled: boolean }) {
  return control.type === "enum" ? (
    <EnumRow control={control} disabled={disabled} />
  ) : (
    <SliderRow control={control} disabled={disabled} />
  );
}

function serverValueOf(control: ControlDescriptor): number {
  return control.cur ?? control.value ?? control.min ?? 0;
}

function SliderRow({ control, disabled }: { control: ControlDescriptor; disabled: boolean }) {
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

  const value = isLogScale(control)
    ? snapToStep(control, fromSliderPosition(control, position))
    : fromSliderPosition(control, position);

  const commit = (p: number) => {
    setPosition(p);
    const v = isLogScale(control) ? snapToStep(control, fromSliderPosition(control, p)) : p;
    api.setControl(id, v).catch((e) => console.error(e));
  };

  const { min, max, step } = sliderBounds(control);
  return (
    <div>
      <div className="flex justify-between text-sm mb-1">
        <span>{control.label}</span>
        <span className="text-muted-foreground">
          {formatControlValue(value, control.unit, control.step)}
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

function EnumRow({ control, disabled }: { control: ControlDescriptor; disabled: boolean }) {
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
    api.setControl(id, v).catch((e) => console.error(e));
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
