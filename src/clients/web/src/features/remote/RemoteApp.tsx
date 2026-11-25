import { LobbyView } from './LobbyView';
import { JoinScreen } from './JoinScreen';
import { useStageClient } from './useStageClient';

/** Phone guest entry point served at `/remote?code=XXXX`. */
export function RemoteApp() {
  const api = useStageClient();

  if (!api.state.snapshot) {
    return (
      <JoinScreen
        busy={api.status === 'connecting'}
        error={api.error}
        onJoin={api.join}
      />
    );
  }

  return <LobbyView api={api} />;
}
