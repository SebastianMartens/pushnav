/**
 * Overlay markers are drawn in image pixels (the SVG viewBox is image_w x image_h)
 * and were sized for a 1280 px wide frame. Scale them by image_w / 1280 so they keep
 * their on-screen size at 1936 px (IDS camera; else ~34 % smaller) and at 968 px
 * (2x2 binning; else ~32 % bigger). The edge-arrow margin of the web server scales
 * the same way.
 */
export const OVERLAY_REFERENCE_WIDTH = 1280;

export function overlayScale(imageW: number): number {
  return Number.isFinite(imageW) && imageW > 0 ? imageW / OVERLAY_REFERENCE_WIDTH : 1;
}

/** SVG transform that scales whatever is drawn around (x, y) by k, leaving (x, y) in place. */
export function scaleAbout(x: number, y: number, k: number): string {
  return `translate(${x} ${y}) scale(${k}) translate(${-x} ${-y})`;
}
