import { useState } from "react";
import { TriangleAlert, X } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import type { ServerError } from "@/lib/types";

interface Props {
  errors: ServerError[];
}

/**
 * Warning for integration servers (Stellarium, LX200) that failed to start,
 * typically because their port is already in use. Dismissible per page load;
 * the same message does not come back until the page is reloaded.
 */
export function ServerErrorBanner({ errors }: Props) {
  const [dismissed, setDismissed] = useState<ReadonlySet<string>>(new Set());
  const keyOf = (e: ServerError) => `${e.server}:${e.port}:${e.message}`;
  const visible = errors.filter((e) => !dismissed.has(keyOf(e)));
  if (visible.length === 0) return null;

  return (
    <div className="flex flex-col gap-1 pb-2">
      {visible.map((e) => (
        <Alert
          key={keyOf(e)}
          variant="destructive"
          className="border-destructive/60 pr-9"
          data-testid="server-error"
        >
          <TriangleAlert />
          <AlertDescription>{e.message}</AlertDescription>
          <button
            type="button"
            aria-label="Dismiss"
            className="absolute top-2 right-2 text-destructive/80 hover:text-destructive"
            onClick={() => setDismissed((prev) => new Set(prev).add(keyOf(e)))}
          >
            <X className="size-4" />
          </button>
        </Alert>
      ))}
    </div>
  );
}
