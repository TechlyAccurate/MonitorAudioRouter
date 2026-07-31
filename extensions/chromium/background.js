"use strict";

// Chromium bridge:
// - find audible tabs grouped by browser window;
// - include OS process IDs only when Chromium exposes them;
// - send local hints to the tray app through native messaging.
const NATIVE_HOST_NAME = "com.monitoraudiorouter.router";
const SNAPSHOT_INTERVAL_MS = 1500;

let nativeHostPort = null;
let nativeHostReconnectTimer = null;
let hasTriedProcessPermission = false;

// Browser identity

function detectBrowserName() {
  const userAgent = navigator.userAgent;
  if (userAgent.includes("Edg/")) {
    return "edge";
  }

  if (userAgent.includes("Firefox/")) {
    return "firefox";
  }

  return "chrome";
}

// Chrome callback APIs

function callChromeApi(chromeApiFunction, ...chromeApiArguments) {
  return new Promise((resolve, reject) => {
    let hasSettled = false;
    const resolveOnce = (value) => {
      if (!hasSettled) {
        hasSettled = true;
        resolve(value);
      }
    };
    const rejectOnce = (error) => {
      if (!hasSettled) {
        hasSettled = true;
        reject(error);
      }
    };

    try {
      const possiblePromise = chromeApiFunction(...chromeApiArguments, (apiResult) => {
        const error = chrome.runtime.lastError;
        if (error) {
          rejectOnce(new Error(error.message));
          return;
        }

        resolveOnce(apiResult);
      });

      if (possiblePromise && typeof possiblePromise.then === "function") {
        possiblePromise.then(resolveOnce, rejectOnce);
      }
    } catch (error) {
      rejectOnce(error);
    }
  });
}

// Native messaging connection

function connectNativeHost() {
  if (nativeHostPort) {
    return;
  }

  try {
    const nextPort = chrome.runtime.connectNative(NATIVE_HOST_NAME);
    nativeHostPort = nextPort;
    nextPort.onDisconnect.addListener(() => {
      if (nativeHostPort === nextPort) {
        nativeHostPort = null;
      }

      scheduleReconnect();
    });
  } catch {
    nativeHostPort = null;
    scheduleReconnect();
  }
}

function scheduleReconnect() {
  if (nativeHostReconnectTimer !== null) {
    return;
  }

  // Keep one reconnect timer alive at most. This prevents stale extension
  // workers from repeatedly launching native-host helper processes.
  nativeHostReconnectTimer = setTimeout(() => {
    nativeHostReconnectTimer = null;
    if (!nativeHostPort) {
      connectNativeHost();
    }
  }, 2500);
}

function postToNativeHost(message) {
  if (!nativeHostPort) {
    connectNativeHost();
  }

  if (!nativeHostPort) {
    return;
  }

  const connectedNativeHostPort = nativeHostPort;
  try {
    connectedNativeHostPort.postMessage(message);
  } catch {
    if (nativeHostPort === connectedNativeHostPort) {
      nativeHostPort = null;
    }

    scheduleReconnect();
  }
}

// Optional process hints

async function processIdsForTabs(tabs) {
  const processesApi = chrome.processes;
  if (!processesApi ||
      typeof processesApi.getProcessIdForTab !== "function" ||
      typeof processesApi.getProcessInfo !== "function") {
    return [];
  }

  const browserProcessIds = [];
  for (const tab of tabs) {
    try {
      const processId = await callChromeApi(processesApi.getProcessIdForTab, tab.id);
      if (Number.isInteger(processId) && !browserProcessIds.includes(processId)) {
        browserProcessIds.push(processId);
      }
    } catch {
      // The API is optional and may not be available on stable Chromium builds.
    }
  }

  if (browserProcessIds.length === 0) {
    return [];
  }

  try {
    const processInfoByBrowserProcessId = await callChromeApi(processesApi.getProcessInfo, browserProcessIds, false);
    return Object.values(processInfoByBrowserProcessId || {})
      .map((info) => info && info.osProcessId)
      .filter((processId, index, values) =>
        Number.isInteger(processId) && processId > 0 && values.indexOf(processId) === index);
  } catch {
    return [];
  }
}

async function collectAudibleWindows() {
  // Audible window collection
  const tabs = await callChromeApi(chrome.tabs.query, {});
  const audibleTabs = tabs.filter((tab) => tab.audible && tab.windowId !== undefined);
  const tabsByWindowId = new Map();

  for (const tab of audibleTabs) {
    const tabsForWindow = tabsByWindowId.get(tab.windowId) || [];
    tabsForWindow.push(tab);
    tabsByWindowId.set(tab.windowId, tabsForWindow);
  }

  const windows = [];
  for (const [windowId, windowTabs] of tabsByWindowId) {
    try {
      const browserWindow = await callChromeApi(chrome.windows.get, windowId);
      if (!Number.isFinite(browserWindow.left) || !Number.isFinite(browserWindow.top) ||
          !Number.isFinite(browserWindow.width) || !Number.isFinite(browserWindow.height)) {
        continue;
      }

      // Titles are local matching hints. The extension does not send URLs,
      // page contents, history, cookies, or anything to a remote service.
      const activeTabTitles = [...new Set(tabs
        .filter((tab) => tab.windowId === windowId && tab.active)
        .map((tab) => tab.title)
        .filter((title) => typeof title === "string" && title.trim().length > 0)
        .map((title) => title.trim()))];
      const audibleTabTitles = [...new Set(windowTabs
        .map((tab) => tab.title)
        .filter((title) => typeof title === "string" && title.trim().length > 0)
        .map((title) => title.trim()))];

      windows.push({
        windowId,
        left: browserWindow.left,
        top: browserWindow.top,
        width: browserWindow.width,
        height: browserWindow.height,
        processIds: await processIdsForTabs(windowTabs),
        titles: audibleTabTitles,
        windowTitles: activeTabTitles
      });
    } catch {
      // Window may have closed during collection.
    }
  }

  return windows;
}

// Snapshot scheduling

async function sendSnapshot() {
  try {
    postToNativeHost({
      type: "audibleWindows",
      browser: detectBrowserName(),
      sentAt: Date.now(),
      windows: await collectAudibleWindows()
    });
  } catch {
    // The next scheduled pass will retry.
  }
}

// Extension entry point

function start() {
  connectNativeHost();
  sendSnapshot();
  setInterval(sendSnapshot, SNAPSHOT_INTERVAL_MS);

  chrome.tabs.onUpdated.addListener(sendSnapshot);
  chrome.tabs.onActivated.addListener(sendSnapshot);
  chrome.tabs.onAttached.addListener(sendSnapshot);
  chrome.tabs.onDetached.addListener(sendSnapshot);
  chrome.tabs.onRemoved.addListener(sendSnapshot);
  chrome.windows.onBoundsChanged.addListener(sendSnapshot);
  chrome.windows.onRemoved.addListener(sendSnapshot);

  chrome.action.onClicked.addListener(() => {
    // The unpacked developer build can request the optional processes
    // permission on click. Store packages strip this listener body and send
    // snapshots without asking.
    if (hasTriedProcessPermission || !chrome.permissions) {
      sendSnapshot();
      return;
    }

    hasTriedProcessPermission = true;
    chrome.permissions.request({ permissions: ["processes"] }, () => {
      sendSnapshot();
    });
  });
}

start();
