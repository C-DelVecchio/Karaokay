/**
 * Small trailing debouncer for coalescing high-frequency UI events into one network send.
 *
 * `schedule()` keeps the latest value and restarts the delay; the value is delivered once the input
 * has been quiet for `delayMs`. `flush()` delivers a pending value immediately (for a final commit
 * on unmount) and `cancel()` drops it. Local UI updates stay synchronous at the call site — only
 * the send is delayed.
 */
export interface TrailingDebouncer<T> {
  schedule(value: T): void;
  /** Deliver the pending value now, if any. */
  flush(): void;
  /** Drop the pending value without delivering it. */
  cancel(): void;
  readonly pending: boolean;
}

export function createTrailingDebounce<T>(
  delayMs: number,
  send: (value: T) => void,
): TrailingDebouncer<T> {
  if (!Number.isFinite(delayMs) || delayMs < 0) {
    throw new RangeError('Trailing debounce delay must be a non-negative number.');
  }

  let timer: ReturnType<typeof setTimeout> | null = null;
  let pendingValue: T | undefined;
  let hasPending = false;

  const deliver = () => {
    if (!hasPending) {
      return;
    }

    const value = pendingValue as T;
    hasPending = false;
    pendingValue = undefined;
    send(value);
  };

  return {
    schedule(value) {
      pendingValue = value;
      hasPending = true;
      if (timer !== null) {
        clearTimeout(timer);
      }

      timer = setTimeout(() => {
        timer = null;
        deliver();
      }, delayMs);
    },
    flush() {
      if (timer !== null) {
        clearTimeout(timer);
        timer = null;
      }

      deliver();
    },
    cancel() {
      if (timer !== null) {
        clearTimeout(timer);
        timer = null;
      }

      hasPending = false;
      pendingValue = undefined;
    },
    get pending() {
      return hasPending;
    },
  };
}
