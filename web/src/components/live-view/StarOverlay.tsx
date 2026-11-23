import type { EnginePayload } from "@/lib/types";
import { overlayScale } from "@/lib/overlayScale"; // fork: camera-controls

interface Props {
  state: EnginePayload;
}

/**
 * All detected centroids (dim red) and matched centroids (bright red).
 * Mirrors window.py _draw_star_overlay (~1828).
 */
export function StarOverlay({ state }: Props) {
  const k = overlayScale(state.image_w); // markers are sized for a 1280 px wide frame
  const all = state.camera.all_centroids ?? [];
  const matched = state.camera.matched_centroids ?? [];
  const matchedKeys = new Set(
    matched.map(([y, x]) => `${y.toFixed(1)}:${x.toFixed(1)}`),
  );
  return (
    <g>
      {all.map(([y, x], i) => {
        const isMatched = matchedKeys.has(`${y.toFixed(1)}:${x.toFixed(1)}`);
        return (
          <circle
            key={i}
            cx={x}
            cy={y}
            r={(isMatched ? 8 : 6) * k}
            fill="none"
            stroke={
              isMatched
                ? "rgba(255, 70, 70, 0.86)"
                : "rgba(150, 40, 40, 0.71)"
            }
            strokeWidth={(isMatched ? 2 : 1) * k}
          />
        );
      })}
    </g>
  );
}
