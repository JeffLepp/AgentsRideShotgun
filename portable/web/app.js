const $ = (id) => document.getElementById(id);
const SKINS = new Set(['windows', 'mac', 'linux']);
const STORAGE_KEY = 'deskweave.appearance';
let status = null;
let statusFresh = false;
let authenticated = false;
let busy = '';
let collapsed = false;
let compact = false;
let ownerInput = false;
let noticeKind = '';
let pollTimer = null;
let statusPromise = null;
let viewer = null;
let viewerConnected = false;
let viewerConnecting = false;
let viewerGeneration = 0;
let viewerRetry = null;
let viewerSetupError = false;
let RFBClass = null;
let rfbImport = null;
let frameTimer = null;
let frameAbort = null;
let framePending = false;
let frameGeneration = 0;
let currentFrame = null;
const frameUrls = new Set();
let inputGeneration = 0;
let inputQueue = Promise.resolve();
let queuedInputs = 0;
let composing = false;
let pointerStart = null;
let wheelTimer = null;
let wheelX = 0;
let wheelY = 0;
let savedSkin = readSkin();
let reportedUrl = null;

function readSkin() {
  try { const skin = localStorage.getItem(STORAGE_KEY); return SKINS.has(skin) ? skin : null; }
  catch { return null; }
}

function platform(value) {
  const name = String(value || '').toLowerCase();
  if (/darwin|mac/.test(name)) return 'mac';
  if (/win/.test(name)) return 'windows';
  return 'linux';
}

function platformName(value) {
  if (typeof value !== 'string' || !value.trim()) return 'Unavailable';
  return ({ mac: 'Mac', windows: 'Windows', linux: 'Linux' })[platform(value)];
}

function applySkin(skin, persist = false) {
  if (!SKINS.has(skin)) skin = 'windows';
  document.documentElement.dataset.skin = skin;
  $('appearance').value = skin;
  if (persist) {
    savedSkin = skin;
    try { localStorage.setItem(STORAGE_KEY, skin); } catch { /* Appearance still works for this page. */ }
  }
  if (viewer) viewer.background = getComputedStyle(document.documentElement).getPropertyValue('--canvas').trim();
}

function text(value, fallback = 'Unavailable') {
  return typeof value === 'string' && value.trim() ? value.trim() : fallback;
}

function positive(value) { return typeof value === 'number' && Number.isFinite(value) && value > 0; }
function memory(value) {
  if (!positive(value)) return 'Unavailable';
  return value >= 1024 ** 3 ? `${(value / 1024 ** 3).toFixed(1)} GiB` : `${Math.ceil(value / 1024 ** 2)} MiB`;
}
function elapsed(value) {
  if (!positive(value)) return 'Unavailable';
  if (value < 60) return `${value.toFixed(1)} s`;
  if (value < 3600) return `${Math.floor(value / 60)}m ${Math.floor(value % 60)}s`;
  return `${Math.floor(value / 3600)}h ${Math.floor((value % 3600) / 60)}m`;
}
function started(value) {
  if (!value) return 'Unavailable';
  const date = new Date(value);
  return Number.isFinite(date.getTime()) ? date.toLocaleString([], { dateStyle: 'short', timeStyle: 'short' }) : 'Unavailable';
}

async function api(path, body, timeout = 15000) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeout);
  try {
    const response = await fetch(path, {
      method: body === undefined ? 'GET' : 'POST', credentials: 'same-origin', cache: 'no-store',
      headers: body === undefined ? { Accept: 'application/json' } : {
        Accept: 'application/json', 'Content-Type': 'application/json', 'X-Deskweave-Request': '1'
      },
      body: body === undefined ? undefined : JSON.stringify(body), signal: controller.signal
    });
    let data = null;
    try { data = await response.json(); }
    catch { /* Authentication middleware can return a plain-text error. */ }
    if (!response.ok) {
      const error = new Error(response.status === 401
        ? 'Open Deskweave using the authenticated link from its launcher.'
        : text(data?.error, `The local service could not complete this request (${response.status}).`));
      error.status = response.status;
      throw error;
    }
    if (data === null) throw new Error('The local service returned an unreadable response.');
    return data;
  } catch (error) {
    if (error.name === 'AbortError') throw new Error('The local service took too long to respond. Check that Deskweave is still running.');
    if (error instanceof TypeError) throw new Error('The local service is unavailable. Check that Deskweave is still running.');
    throw error;
  } finally { clearTimeout(timer); }
}

function showNotice(message, kind = 'action') {
  noticeKind = kind;
  $('noticeText').textContent = String(message).slice(0, 700);
  $('notice').hidden = false;
  $('retryButton').textContent = viewerSetupError ? 'Reload' : 'Retry';
}

function clearNotice(kind) {
  if (kind && kind !== noticeKind) return;
  noticeKind = '';
  $('notice').hidden = true;
  $('noticeText').textContent = '';
}

function framesTransport() { return status?.transport === 'frames'; }
function visibleViewer() { return !document.hidden && !collapsed; }
function wantsViewer() { return authenticated && statusFresh && status?.running === true && visibleViewer(); }
function canControl() { return wantsViewer() && ownerInput && status?.controller === 'owner' && !$('stopDialog').open; }

function revokeInput() {
  ownerInput = false;
  inputGeneration++;
  composing = false;
  $('textInput').value = '';
  clearTimeout(wheelTimer);
  wheelTimer = null;
  wheelX = wheelY = 0;
  if (viewer) { viewer.viewOnly = true; viewer.focusOnClick = false; viewer.blur(); }
  $('textInput').blur();
  syncInput();
}

function syncInput() {
  const enabled = canControl();
  if (viewer) { viewer.viewOnly = !enabled; viewer.focusOnClick = enabled; }
  $('textInput').disabled = !enabled;
  $('frameSurface').setAttribute('aria-label', enabled
    ? 'Browser workspace. Click or type to interact.' : 'Browser workspace. Take control before clicking or typing.');
  $('controlHint').hidden = !viewerConnected || enabled || !wantsViewer();
  $('controllerLabel').textContent = enabled ? 'Your control' : status?.controller === 'agent' ? 'Agent control' : 'View only';
  $('takeoverButton').textContent = enabled ? 'Your control' : 'Take control';
  $('drawerTakeover').textContent = enabled ? 'You have control' : 'Take control';
  $('takeoverButton').disabled = !statusFresh || !status?.running || !!busy || enabled;
  $('drawerTakeover').disabled = !statusFresh || !status?.running || !!busy || enabled;
  $('releaseButton').hidden = !statusFresh || status?.controller !== 'owner';
  $('drawerRelease').hidden = !statusFresh || status?.controller !== 'owner';
  $('releaseButton').disabled = !!busy;
  $('drawerRelease').disabled = !!busy;
  $('terminalButton').disabled = !enabled || !!busy || framesTransport();
  $('browserButton').disabled = !enabled || !!busy;
  $('navigateButton').disabled = !enabled || !!busy;
  $('browserUrl').disabled = !enabled || !!busy;
  // The runtime owns whether there is anywhere to go; refusing here would only guess at it.
  for (const id of ['backButton', 'forwardButton', 'reloadButton'])
    $(id).disabled = !enabled || !!busy || !status?.historySupported;
  $('terminalButton').title = framesTransport() ? 'This browser workspace supports web actions; a terminal is unavailable.'
    : enabled ? 'Open a terminal in the workspace' : 'Take control to open a terminal';
  $('browserButton').title = enabled ? 'Open a browser in the workspace' : 'Take control to open a browser';
  $('navigationForm').title = enabled ? 'Open a website in the workspace' : 'Take control to open a website';
}

function render() {
  const running = statusFresh && status?.running === true;
  const ready = authenticated && statusFresh;
  const frames = framesTransport();
  $('workspaceTitle').textContent = frames || status?.workload === 'web' ? 'Browser workspace' : 'Linux desktop';
  $('powerButton').disabled = !ready || !!busy;
  $('powerButton').textContent = busy === 'start' ? 'Starting…' : busy === 'stop' ? 'Stopping…' : running ? 'Stop workspace' : 'Start workspace';
  $('powerButton').className = running ? 'secondary-button' : 'primary-button';
  $('takeoverButton').disabled = !running || !!busy;
  $('drawerTakeover').disabled = !running || !!busy;
  $('agentToggle').disabled = !ready || !!busy;
  $('agentToggle').checked = ready && status?.agentEnabled === true;
  const commands = ready && status.commandsSupported === true;
  $('agentToggle').setAttribute('aria-label', commands ? 'Allow agent control and commands' : frames ? 'Allow agent browser control' : 'Allow agent access');
  $('agentPermissions').textContent = !ready ? 'Available agent actions depend on the workspace backend.'
    : commands ? "Includes workspace control and commands with your user account's file permissions. Command limits do not provide a filesystem sandbox."
      : 'Includes browser navigation and interaction. Commands are unavailable in this browser workspace.';
  $('navigationForm').hidden = !frames;
  $('browserInputNote').hidden = !frames;
  if (ready && frames && typeof status.url === 'string' && status.url !== reportedUrl
      && document.activeElement !== $('browserUrl') && busy !== 'navigate') {
    $('browserUrl').value = status.url;
    reportedUrl = status.url;
  }
  $('agentDetail').textContent = !ready ? 'Connect to the local service to change agent access.'
    : !status.agentEnabled ? 'Agent access is off. Enable it when you want an agent to use this workspace.'
      : status.controller === 'owner' ? 'Agent access is enabled. Release control to let an agent act.'
        : 'Agent access is enabled. An agent can request control of this workspace.';
  $('controllerDetail').textContent = !ready ? 'Unavailable'
    : status.controller === 'owner' ? 'Owner' : status.controller === 'agent' ? 'Agent' : 'No controller';
  const action = ready ? text(status.activeAction, '') : '';
  $('activeActionRow').hidden = !action;
  $('activeAction').textContent = ({ workspace_start: 'Starting workspace', workspace_run: 'Running a command',
    workspace_launch: 'Opening an application', workspace_navigate: 'Opening a website', workspace_input: 'Sending input' })[action] || action;
  $('desktopStage').setAttribute('aria-label', frames ? 'Live browser workspace' : 'Live Linux workspace desktop');
  $('stateDot').dataset.state = !ready && noticeKind ? 'error' : running ? 'running' : 'stopped';

  const measurements = ready && running;
  $('processCount').textContent = measurements && positive(status.processCount) ? String(status.processCount) : 'Unavailable';
  $('rssBytes').textContent = measurements ? memory(status.rssBytes) : 'Unavailable';
  $('containerMemoryRow').hidden = !ready || !positive(status.containerMemoryBytes);
  $('containerMemory').textContent = ready ? memory(status.containerMemoryBytes) : 'Unavailable';
  $('cpuSeconds').textContent = measurements ? elapsed(status.cpuSeconds) : 'Unavailable';
  $('memoryLimit').textContent = ready && positive(status.limits?.memoryMiB) ? memory(status.limits.memoryMiB * 1024 ** 2) : 'Unavailable';
  $('desktopSize').textContent = measurements && positive(status.width) && positive(status.height) ? `${status.width} × ${status.height}` : 'Unavailable';
  $('startedAt').textContent = measurements ? started(status.startedAt) : 'Unavailable';
  const sampleAge = measurements && positive(status.metricsAgeSeconds) ? status.metricsAgeSeconds : 0;
  $('metricsSampleNote').hidden = !measurements || (!status.activeAction && sampleAge < 3);
  $('metricsSampleNote').textContent = sampleAge >= 1
    ? `Resource sample: ${Math.floor(sampleAge)} seconds ago. Updates resume after the current action.`
    : 'Resource sample held while the current action finishes.';
  $('hostDetail').textContent = ready ? platformName(status.hostPlatform) : 'Unavailable';
  $('backendDetail').textContent = ready ? text(status.backend) : 'Unavailable';
  $('isolationDetail').textContent = ready ? text(status.isolation) : 'Unavailable';
  $('versionLabel').textContent = ready && text(status.version, '') ? `Deskweave ${status.version} · Portable pilot` : 'Deskweave portable pilot';
  $('hostLabel').textContent = ready ? `${platformName(status.hostPlatform)} host` : 'Portable workspace';
  $('environmentNote').textContent = frames
    ? 'This browser runs through the reported backend. Appearance changes this interface, not the host platform.'
    : 'The workspace shown here runs Linux. Appearance changes this interface, not the host platform.';
  $('placeholderFootnote').textContent = frames ? 'A browser workspace on your host.' : 'A separate Linux desktop on your host.';
  $('statusSecondary').textContent = running && positive(status.width) && positive(status.height)
    ? `${status.width} × ${status.height} · ${frames ? 'Browser workspace' : 'Linux desktop'}` : 'Local workspace viewer';

  let title = 'Opening your workspace';
  let detail = 'Connecting to the local Deskweave service.';
  let connection = 'Connecting to the local service';
  $('placeholderAction').hidden = true;
  if (!authenticated || !statusFresh) {
    if (noticeKind) { title = authenticated ? 'Local service unavailable' : 'Open from your launcher'; detail = 'Use the authenticated Deskweave link and keep the local service running.'; connection = 'Local connection unavailable'; }
  } else if (!running) {
    title = frames ? 'Your browser workspace' : 'Your Linux workspace';
    detail = frames ? 'Start a workspace, then take control to open a website.' : 'Start a desktop, then take control to open Terminal or Browser.';
    connection = busy === 'start' ? 'Starting the workspace' : 'Workspace stopped';
    $('placeholderAction').hidden = false;
    $('placeholderAction').disabled = !!busy;
  } else if (viewerSetupError && !frames) {
    title = 'Desktop viewer needs setup';
    detail = 'Install the bundled desktop viewer with the portable setup command, then reload this page.';
    connection = 'Workspace running · viewer unavailable';
  } else if (collapsed || document.hidden) {
    title = 'Viewer paused'; detail = 'Your workspace is still running.'; connection = 'Viewer paused · workspace running';
  } else if (viewerConnected) {
    connection = canControl() ? 'Connected · your control' : 'Connected · view only';
  } else {
    title = 'Connecting to the workspace';
    detail = 'The desktop will appear here when the viewer connects.';
    connection = 'Workspace running · connecting viewer';
  }
  $('placeholderTitle').textContent = title;
  $('placeholderDetail').textContent = detail;
  $('viewerPlaceholder').hidden = viewerConnected && wantsViewer();
  $('connectionStatus').textContent = connection;
  syncInput();
}

async function refreshStatus(force = false) {
  if (statusPromise) { await statusPromise; if (!force) return; }
  const operation = (async () => {
    try {
      const next = await api('/api/status', undefined, 10000);
      if (!next || typeof next.running !== 'boolean') throw new Error('The local service returned an incomplete workspace status.');
      const changedSession = status && (next.transport !== status.transport || next.startedAt !== status.startedAt || next.running !== status.running);
      if (changedSession) { revokeInput(); disconnectViewer(); }
      if (next.controller !== 'owner') revokeInput();
      status = next;
      statusFresh = true;
      authenticated = true;
      if (!savedSkin) applySkin(platform(next.hostPlatform));
      clearNotice('service');
      if (typeof next.error === 'string' && next.error.trim()) showNotice(next.error, 'backend');
      else clearNotice('backend');
      render();
      ensureViewer();
    } catch (error) {
      statusFresh = false;
      if (error.status === 401) authenticated = false;
      revokeInput();
      disconnectViewer();
      showNotice(error.message, 'service');
      render();
    }
  })();
  statusPromise = operation;
  try { await operation; } finally { if (statusPromise === operation) statusPromise = null; }
}

function schedulePoll() {
  clearTimeout(pollTimer);
  if (document.hidden) return;
  pollTimer = setTimeout(async () => { await refreshStatus(); schedulePoll(); }, collapsed ? 10000 : 2000);
}

async function mutate(kind, path, body, timeout = 30000) {
  if (busy || !authenticated || !statusFresh) return false;
  busy = kind;
  clearNotice('action');
  render();
  try {
    await api(path, body, timeout);
    await refreshStatus(true);
    return statusFresh;
  } catch (error) {
    if (error.status === 401) { authenticated = false; statusFresh = false; revokeInput(); disconnectViewer(); }
    showNotice(error.message, 'action');
    return false;
  } finally { busy = ''; render(); }
}

async function startWorkspace() { await mutate('start', '/api/start', {}, 90000); }
async function requestStop() {
  if (busy || !status?.running) return;
  $('stopDetail').textContent = status.controller === 'agent'
    ? 'An agent has control. Stopping will end its current work and close the workspace applications.'
    : 'Open applications will close. Save any work before stopping.';
  $('stopDialog').returnValue = '';
  $('stopDialog').showModal();
  syncInput();
}

async function takeover() {
  if (canControl()) return;
  if (await mutate('takeover', '/api/takeover', {}, 90000)) {
    ownerInput = status?.controller === 'owner';
    inputGeneration++;
    syncInput();
    render();
    // Focus follows this explicit owner action; reconnect and background polls never focus.
    if (canControl() && document.hasFocus()) {
      if (framesTransport()) $('textInput').focus({ preventScroll: true });
      else if (viewerConnected && viewer) viewer.focus();
    }
  }
}

async function releaseControl() {
  if (busy || status?.controller !== 'owner') return;
  revokeInput();
  await mutate('release', '/api/release', {});
  render();
}

function disconnectViewer() {
  viewerGeneration++;
  viewerConnecting = false;
  viewerConnected = false;
  clearTimeout(viewerRetry);
  viewerRetry = null;
  const previous = viewer;
  viewer = null;
  if (previous) { previous.viewOnly = true; previous.blur(); previous.disconnect(); }
  stopFrames();
}

function ensureViewer() {
  if (!wantsViewer()) { disconnectViewer(); return; }
  $('screen').hidden = framesTransport();
  $('frameSurface').hidden = !framesTransport();
  if (framesTransport()) { if (!frameTimer && !framePending) fetchFrame(); }
  else connectRfb();
}

async function connectRfb() {
  if (viewer || viewerConnecting || viewerRetry || viewerSetupError || !wantsViewer() || framesTransport()) return;
  viewerConnecting = true;
  const generation = ++viewerGeneration;
  try {
    if (!RFBClass) {
      rfbImport ??= import('/vendor/novnc/core/rfb.js');
      try { RFBClass = (await rfbImport).default; }
      catch {
        viewerSetupError = true;
        throw new Error('The bundled desktop viewer could not load. Run portable setup, then reload this page.');
      }
    }
    if (generation !== viewerGeneration || !wantsViewer() || framesTransport()) return;
    const connection = await api('/api/connect', {});
    if (generation !== viewerGeneration || !wantsViewer() || framesTransport()) return;
    if (typeof connection.password !== 'string') throw new Error('The service did not provide a desktop viewer connection.');
    const url = new URL('/rfb', location.href);
    url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const client = new RFBClass($('screen'), url.href, { credentials: { password: connection.password }, shared: true });
    viewer = client;
    client.viewOnly = true;
    client.focusOnClick = false;
    client.resizeSession = false;
    client.scaleViewport = true;
    client.background = getComputedStyle(document.documentElement).getPropertyValue('--canvas').trim();
    client.addEventListener('connect', () => {
      if (viewer !== client) return;
      viewerConnected = true;
      clearNotice('viewer');
      render();
    });
    client.addEventListener('disconnect', (event) => {
      if (viewer !== client) return;
      viewer = null;
      viewerConnected = false;
      revokeInput();
      if (wantsViewer()) {
        if (!event.detail?.clean) showNotice('The viewer disconnected. Reconnecting to the same workspace…', 'viewer');
        viewerRetry = setTimeout(() => { viewerRetry = null; ensureViewer(); }, 2500);
      }
      render();
    });
    client.addEventListener('securityfailure', () => {
      if (viewer === client) showNotice('The desktop connection could not be authenticated. Retry the local connection.', 'viewer');
    });
    syncInput();
  } catch (error) {
    if (generation !== viewerGeneration) return;
    showNotice(error.message, 'viewer');
    if (!viewerSetupError && wantsViewer()) viewerRetry = setTimeout(() => { viewerRetry = null; ensureViewer(); }, 3000);
  } finally {
    if (generation === viewerGeneration) { viewerConnecting = false; render(); }
  }
}

function stopFrames() {
  frameGeneration++;
  clearTimeout(frameTimer);
  frameTimer = null;
  frameAbort?.abort();
  frameAbort = null;
  $('frameImage').removeAttribute('src');
  currentFrame = null;
  for (const url of frameUrls) URL.revokeObjectURL(url);
  frameUrls.clear();
}

async function fetchFrame() {
  if (framePending || !wantsViewer() || !framesTransport()) return;
  framePending = true;
  frameTimer = null;
  const generation = frameGeneration;
  const controller = new AbortController();
  frameAbort = controller;
  const timeout = setTimeout(() => controller.abort(), 12000);
  try {
    const response = await fetch('/api/frame', { credentials: 'same-origin', cache: 'no-store', headers: { Accept: 'image/png' }, signal: controller.signal });
    if (!response.ok) throw new Error(`The browser frame could not be read (${response.status}).`);
    if (!response.headers.get('Content-Type')?.toLowerCase().includes('image/png')) throw new Error('The service did not return a browser image.');
    const blob = await response.blob();
    if (generation !== frameGeneration || !wantsViewer() || !framesTransport()) return;
    const url = URL.createObjectURL(blob);
    frameUrls.add(url);
    currentFrame = url;
    $('frameImage').onload = () => {
      if (generation !== frameGeneration || currentFrame !== url) return;
      for (const old of frameUrls) if (old !== url) { URL.revokeObjectURL(old); frameUrls.delete(old); }
      viewerConnected = true;
      clearNotice('viewer');
      render();
    };
    $('frameImage').onerror = () => {
      if (generation === frameGeneration) { viewerConnected = false; showNotice('The browser image could not be displayed. Retrying…', 'viewer'); render(); }
      URL.revokeObjectURL(url); frameUrls.delete(url);
    };
    $('frameImage').src = url;
  } catch (error) {
    if (generation !== frameGeneration || !wantsViewer()) return;
    viewerConnected = false;
    revokeInput();
    showNotice(error.name === 'AbortError' ? 'The browser image took too long. Retrying…' : error.message, 'viewer');
    render();
  } finally {
    clearTimeout(timeout);
    if (frameAbort === controller) frameAbort = null;
    framePending = false;
    // The workspace's own frame rate, not a fixed half-second. --fps used to be accepted,
    // reported in status, and then ignored by the only loop that decided cadence.
    const fps = positive(status?.fps) ? Math.min(30, status.fps) : 12;
    if (wantsViewer() && framesTransport()) frameTimer = setTimeout(fetchFrame, Math.round(1000 / fps));
  }
}

function enqueueInput(input) {
  if (!canControl() || !framesTransport()) return;
  if (queuedInputs >= 128) { revokeInput(); showNotice('Input paused because the browser could not keep up. Take control again when ready.'); render(); return; }
  const generation = inputGeneration;
  queuedInputs++;
  inputQueue = inputQueue.then(async () => {
    if (generation !== inputGeneration || !canControl()) return;
    try { await api('/api/input', input, 10000); }
    catch (error) {
      if (generation === inputGeneration) { revokeInput(); showNotice(`Input paused. ${error.message}`); render(); }
    }
  }).finally(() => { queuedInputs--; });
}

function framePoint(event) {
  if (!positive(status?.width) || !positive(status?.height)) return null;
  const rect = $('frameSurface').getBoundingClientRect();
  const scale = Math.min(rect.width / status.width, rect.height / status.height);
  if (!Number.isFinite(scale) || scale <= 0) return null;
  const x = (event.clientX - rect.left - (rect.width - status.width * scale) / 2) / scale;
  const y = (event.clientY - rect.top - (rect.height - status.height * scale) / 2) / scale;
  return x >= 0 && y >= 0 && x < status.width && y < status.height ? { x: Math.floor(x), y: Math.floor(y) } : null;
}

function wireFrameInput() {
  const surface = $('frameSurface');
  const keyboard = $('textInput');
  surface.addEventListener('focus', () => {
    if (canControl()) keyboard.focus({ preventScroll: true });
  });
  surface.addEventListener('pointerdown', (event) => {
    if (!canControl()) return;
    pointerStart = { x: event.clientX, y: event.clientY, button: event.button, id: event.pointerId };
    event.preventDefault();
  });
  surface.addEventListener('pointerup', (event) => {
    const origin = pointerStart;
    pointerStart = null;
    if (!origin || origin.id !== event.pointerId || !canControl()) return;
    const point = framePoint(event);
    if (!point || origin.button < 0 || origin.button > 2) return;
    // Past the slop a press is a drag, which is how text gets selected and a slider moves.
    if (Math.hypot(event.clientX - origin.x, event.clientY - origin.y) > 5) {
      const from = framePoint({ clientX: origin.x, clientY: origin.y });
      if (from && origin.button === 0) enqueueInput({ kind: 'drag', ...from, toX: point.x, toY: point.y });
      keyboard.focus({ preventScroll: true });
      event.preventDefault();
      return;
    }
    enqueueInput({ kind: 'click', ...point, button: origin.button + 1 });
    keyboard.focus({ preventScroll: true });
    event.preventDefault();
  });
  surface.addEventListener('pointercancel', () => { pointerStart = null; });
  surface.addEventListener('contextmenu', (event) => { if (canControl()) event.preventDefault(); });
  surface.addEventListener('wheel', (event) => {
    if (!canControl()) return;
    event.preventDefault();
    const divisor = event.deltaMode === 1 ? 3 : event.deltaMode === 2 ? .1 : 80;
    wheelX += event.deltaX / divisor; wheelY += event.deltaY / divisor;
    if (!wheelTimer) wheelTimer = setTimeout(() => {
      wheelTimer = null;
      const dx = Math.max(-10, Math.min(10, Math.round(wheelX) || Math.sign(wheelX)));
      const dy = Math.max(-10, Math.min(10, Math.round(wheelY) || Math.sign(wheelY)));
      wheelX = wheelY = 0;
      if (dx || dy) enqueueInput({ kind: 'scroll', dx, dy });
    }, 50);
  }, { passive: false });
  surface.addEventListener('keydown', (event) => {
    if (!canControl() || event.isComposing || composing || event.keyCode === 229) return;
    if (['Control', 'Alt', 'Shift', 'Meta', 'CapsLock', 'Dead', 'Unidentified'].includes(event.key)) return;
    // A browser viewer must always leave a keyboard route back to its own controls.
    if (event.key === 'Escape' && !event.ctrlKey && !event.shiftKey && !event.altKey && !event.metaKey) {
      enqueueInput({ kind: 'key', key: 'Escape' });
      keyboard.blur(); $('detailsButton').focus(); event.preventDefault(); event.stopPropagation(); return;
    }
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'v') return;
    if (event.key.length === 1 && ((!event.ctrlKey && !event.metaKey && !event.altKey) || event.getModifierState('AltGraph'))) return;
    const modifiers = [];
    if (event.ctrlKey) modifiers.push('Ctrl');
    if (event.altKey) modifiers.push('Alt');
    if (event.shiftKey) modifiers.push('Shift');
    if (event.metaKey) modifiers.push('Meta');
    enqueueInput({ kind: 'key', key: [...modifiers, event.key].join('+') });
    event.preventDefault();
    event.stopPropagation();
  });
  keyboard.addEventListener('compositionstart', () => { composing = true; });
  keyboard.addEventListener('compositionend', () => {
    composing = false;
    const value = keyboard.value; keyboard.value = '';
    if (value) enqueueInput({ kind: 'text', text: value });
  });
  keyboard.addEventListener('input', (event) => {
    if (composing || event.isComposing) return;
    const value = keyboard.value; keyboard.value = '';
    if (value) enqueueInput({ kind: 'text', text: value });
  });
  keyboard.addEventListener('beforeinput', (event) => {
    if (composing || event.isComposing || !canControl()) return;
    const key = ({ insertLineBreak: 'Enter', insertParagraph: 'Enter', deleteContentBackward: 'Backspace', deleteContentForward: 'Delete' })[event.inputType];
    if (key) { enqueueInput({ kind: 'key', key }); event.preventDefault(); }
  });
  keyboard.addEventListener('paste', (event) => {
    if (!canControl()) return;
    event.preventDefault();
    const value = event.clipboardData?.getData('text/plain');
    keyboard.value = '';
    if (value) enqueueInput({ kind: 'text', text: value });
  });
}

function toggleDetails(open) {
  $('detailsDrawer').hidden = !open;
  $('detailsButton').setAttribute('aria-expanded', String(open));
  if (open) { if (viewer) viewer.blur(); $('closeDetailsButton').focus(); }
  else $('detailsButton').focus();
}

function wireUi() {
  $('appearance').addEventListener('change', (event) => applySkin(event.target.value, true));
  $('compactButton').addEventListener('click', () => {
    compact = !compact;
    $('shell').classList.toggle('is-compact', compact);
    $('compactButton').setAttribute('aria-pressed', String(compact));
    $('compactButton').setAttribute('aria-label', compact ? 'Switch to full view' : 'Switch to compact view');
    $('compactButton').title = compact ? 'Full view' : 'Compact view';
  });
  $('collapseButton').addEventListener('click', async () => {
    collapsed = !collapsed;
    $('shell').classList.toggle('is-collapsed', collapsed);
    $('collapseButton').setAttribute('aria-expanded', String(!collapsed));
    $('collapseButton').setAttribute('aria-label', collapsed ? 'Expand viewer' : 'Collapse viewer');
    $('collapseButton').title = collapsed ? 'Expand viewer' : 'Collapse viewer';
    if (collapsed) { revokeInput(); disconnectViewer(); }
    else await refreshStatus(true);
    render(); schedulePoll();
  });
  $('detailsButton').addEventListener('click', () => toggleDetails($('detailsDrawer').hidden));
  $('closeDetailsButton').addEventListener('click', () => toggleDetails(false));
  $('detailsDrawer').addEventListener('keydown', (event) => { if (event.key === 'Escape') { toggleDetails(false); event.stopPropagation(); } });
  $('powerButton').addEventListener('click', () => status?.running ? requestStop() : startWorkspace());
  $('placeholderAction').addEventListener('click', startWorkspace);
  $('takeoverButton').addEventListener('click', takeover);
  $('drawerTakeover').addEventListener('click', takeover);
  $('releaseButton').addEventListener('click', releaseControl);
  $('drawerRelease').addEventListener('click', releaseControl);
  $('terminalButton').addEventListener('click', () => mutate('launch', '/api/launch', { kind: 'terminal' }));
  $('browserButton').addEventListener('click', () => mutate('launch', '/api/launch', { kind: 'browser' }));
  $('agentToggle').addEventListener('change', (event) => mutate('agent', '/api/agent', { enabled: event.target.checked }));
  $('stopDialog').addEventListener('close', async () => {
    if ($('stopDialog').returnValue === 'stop') {
      revokeInput();
      await mutate('stop', '/api/stop', {}, 60000);
    }
    syncInput(); render();
  });
  $('navigationForm').addEventListener('submit', async (event) => {
    event.preventDefault();
    let value = $('browserUrl').value.trim();
    if (!value) return;
    if (!/^[a-z][a-z\d+.-]*:/i.test(value)) value = 'https://' + value;
    try {
      const url = new URL(value);
      if (!['http:', 'https:'].includes(url.protocol)) throw new Error();
      $('browserUrl').value = url.href;
      await mutate('navigate', '/api/navigate', { url: url.href }, 45000);
    } catch { showNotice('Enter an http:// or https:// website address.'); }
  });
  for (const [id, direction] of [['backButton', 'back'], ['forwardButton', 'forward'], ['reloadButton', 'reload']]) {
    $(id).addEventListener('click', () => mutate('history', '/api/history', { direction }, 45000));
  }
  $('retryButton').addEventListener('click', async () => {
    if (viewerSetupError && !framesTransport()) { location.reload(); return; }
    clearNotice();
    disconnectViewer();
    await refreshStatus(true);
    schedulePoll();
  });
  document.addEventListener('visibilitychange', async () => {
    if (document.hidden) { clearTimeout(pollTimer); revokeInput(); disconnectViewer(); render(); }
    else { await refreshStatus(true); schedulePoll(); }
  });
  window.addEventListener('pagehide', () => { clearTimeout(pollTimer); revokeInput(); disconnectViewer(); });
  wireFrameInput();
}

async function initialize() {
  applySkin(savedSkin || platform(navigator.userAgentData?.platform || navigator.platform));
  wireUi();
  const fragment = new URLSearchParams(location.hash.slice(1));
  let token = fragment.get('token');
  // Remove the launcher credential before any other connection or navigation.
  if (token) history.replaceState(null, '', location.pathname + location.search);
  try {
    if (token) { await api('/api/session', { token }); token = null; }
    await refreshStatus();
  } catch (error) { token = null; showNotice(error.message, 'service'); render(); }
  schedulePoll();
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, { once: true });
else initialize();
