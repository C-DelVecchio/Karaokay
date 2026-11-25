import { useEffect, useState } from 'react';
import { RemoteApp } from './features/remote/RemoteApp';
import { StageShell } from './features/stage/StageShell';

export type Route = 'home' | 'stage' | 'remote' | 'not-found';

/**
 * Minimal pathname router: no react-router dependency for Slice 2. A trailing slash is ignored.
 */
export function resolveRoute(pathname: string): Route {
  const normalized = pathname.replace(/\/+$/, '') || '/';

  switch (normalized) {
    case '/':
      return 'home';
    case '/stage':
      return 'stage';
    case '/remote':
      return 'remote';
    default:
      return 'not-found';
  }
}

function Landing() {
  return (
    <main className="landing">
      <h1>Karaokay</h1>
      <p>Browser karaoke with sweeping lyric fills and guide vocals.</p>
      <a className="landing-cta" href="/stage">
        Enter the stage
      </a>
    </main>
  );
}

export default function App() {
  const [pathname, setPathname] = useState(() => window.location.pathname);

  useEffect(() => {
    const handlePopState = () => setPathname(window.location.pathname);
    window.addEventListener('popstate', handlePopState);
    return () => window.removeEventListener('popstate', handlePopState);
  }, []);

  switch (resolveRoute(pathname)) {
    case 'home':
      return <Landing />;
    case 'stage':
      return <StageShell />;
    case 'remote':
      return <RemoteApp />;
    default:
      return (
        <main>
          <h1>404 — Not found</h1>
        </main>
      );
  }
}
