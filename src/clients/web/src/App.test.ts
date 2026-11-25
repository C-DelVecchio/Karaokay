import { describe, expect, it } from 'vitest';
import { resolveRoute } from './App';

describe('resolveRoute', () => {
  it('maps the landing page', () => {
    expect(resolveRoute('/')).toBe('home');
  });

  it('maps the stage route', () => {
    expect(resolveRoute('/stage')).toBe('stage');
  });

  it('maps the phone remote route', () => {
    expect(resolveRoute('/remote')).toBe('remote');
    expect(resolveRoute('/remote/')).toBe('remote');
  });

  it('ignores a trailing slash', () => {
    expect(resolveRoute('/stage/')).toBe('stage');
  });

  it('falls back to not-found', () => {
    expect(resolveRoute('/does-not-exist')).toBe('not-found');
  });
});
