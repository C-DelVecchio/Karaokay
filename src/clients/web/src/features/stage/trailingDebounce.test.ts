import { afterEach, describe, expect, it, vi } from 'vitest';
import { createTrailingDebounce } from './trailingDebounce';

afterEach(() => {
  vi.useRealTimers();
});

describe('createTrailingDebounce', () => {
  it('coalesces a burst into one trailing send with the last value', () => {
    vi.useFakeTimers();
    const send = vi.fn();
    const debounce = createTrailingDebounce<number>(100, send);

    debounce.schedule(0.1);
    vi.advanceTimersByTime(60);
    debounce.schedule(0.2);
    vi.advanceTimersByTime(60);
    debounce.schedule(0.3);

    expect(send).not.toHaveBeenCalled();
    expect(debounce.pending).toBe(true);

    vi.advanceTimersByTime(99);
    expect(send).not.toHaveBeenCalled();

    vi.advanceTimersByTime(1);
    expect(send).toHaveBeenCalledTimes(1);
    expect(send).toHaveBeenCalledWith(0.3);
    expect(debounce.pending).toBe(false);
  });

  it('flush delivers the pending value immediately and clears the timer', () => {
    vi.useFakeTimers();
    const send = vi.fn();
    const debounce = createTrailingDebounce<number>(100, send);

    debounce.schedule(0.4);
    debounce.flush();

    expect(send).toHaveBeenCalledTimes(1);
    expect(send).toHaveBeenCalledWith(0.4);
    expect(debounce.pending).toBe(false);

    vi.advanceTimersByTime(100);
    expect(send).toHaveBeenCalledTimes(1);
  });

  it('cancel drops the pending value without delivering it', () => {
    vi.useFakeTimers();
    const send = vi.fn();
    const debounce = createTrailingDebounce<number>(100, send);

    debounce.schedule(0.5);
    debounce.cancel();

    expect(debounce.pending).toBe(false);
    vi.advanceTimersByTime(100);
    expect(send).not.toHaveBeenCalled();
  });

  it('flush and cancel are no-ops when nothing is pending', () => {
    vi.useFakeTimers();
    const send = vi.fn();
    const debounce = createTrailingDebounce<number>(100, send);

    debounce.flush();
    debounce.cancel();

    expect(send).not.toHaveBeenCalled();
  });

  it('rejects an invalid delay', () => {
    expect(() => createTrailingDebounce<number>(-1, () => undefined)).toThrow(RangeError);
    expect(() => createTrailingDebounce<number>(Number.NaN, () => undefined)).toThrow(RangeError);
  });
});
