export function createLiveJobConnection({
  applyJob,
  applySimulationState,
  reconnectDelayMs = 2000,
}) {
  let socket = null;
  let reconnectTimer = null;
  let hasAppliedFirstJob = false;
  let cachedContextMeshes = null;
  let pendingSimulationState = null;
  let disposed = false;

  function setStatus(text, className) {
    const element = document.getElementById("liveStatus");
    if (!element) return;
    element.textContent = text;
    element.className = className || "";
  }

  function scheduleReconnect() {
    if (disposed || reconnectTimer) return;
    reconnectTimer = window.setTimeout(() => {
      reconnectTimer = null;
      connect();
    }, reconnectDelayMs);
  }

  function connect() {
    if (disposed || (socket &&
        (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING))) {
      return;
    }

    const protocol = location.protocol === "https:" ? "wss:" : "ws:";
    const session = new URLSearchParams(location.search).get("session") || "default";
    const linkedSession = session !== "default";
    const liveLabel = linkedSession
      ? "live " + session.slice(0, 8)
      : "unlinked: open from Sm01";
    socket = new WebSocket(
      `${protocol}//${location.host}/live/view?session=${encodeURIComponent(session)}`);
    setStatus(linkedSession ? "connecting" : liveLabel,
      linkedSession ? "live-connecting" : "live-disconnected");

    socket.addEventListener("message", event => {
      try {
        const message = JSON.parse(event.data);
        if (message?.type === "simulation") {
          const value = Number(message.simulationParameter);
          if (Number.isFinite(value)) {
            pendingSimulationState = {
              externalControl: message.externalControl !== false,
              simulationParameter: Math.max(0, Math.min(1, value)),
            };
            // The visible job may have arrived from ?path= rather than this socket. The viewer
            // callback safely ignores progress until any job exists, so always offer the update.
            applySimulationState?.(pendingSimulationState);
          }
          setStatus(liveLabel, linkedSession ? "live-connected" : "live-disconnected");
          return;
        }
        const job = message;
        // The server omits unchanged Sm05 context after a viewer has received it once. Restore
        // the existing JS objects before the normal apply path so bounds, camera fitting and
        // rendering continue to see one complete job without parsing/transferring the mesh again.
        if (Array.isArray(job.contextMeshes)) {
          cachedContextMeshes = job.contextMeshes;
        } else if (cachedContextMeshes !== null) {
          job.contextMeshes = cachedContextMeshes;
        }
        applyJob(job, { filePath: null, autoFrame: !hasAppliedFirstJob });
        hasAppliedFirstJob = true;
        // Reapply the exact latest ownership state after a full job. Do not infer ownership from
        // that job's metadata: a cached package can predate an Sm05 disconnect, while the tiny
        // simulation message is the authoritative latest state.
        if (pendingSimulationState !== null)
          applySimulationState?.(pendingSimulationState);
        setStatus(liveLabel, linkedSession ? "live-connected" : "live-disconnected");
      } catch (error) {
        console.error("Live update failed to apply", error);
      }
    });

    socket.addEventListener("close", () => {
      socket = null;
      setStatus("disconnected", "live-disconnected");
      scheduleReconnect();
    });

    // A failed WebSocket connection is followed by close, which owns reconnect scheduling.
    socket.addEventListener("error", () => {});
  }

  function dispose() {
    disposed = true;
    if (reconnectTimer) window.clearTimeout(reconnectTimer);
    reconnectTimer = null;
    socket?.close();
    socket = null;
  }

  return { connect, dispose };
}
