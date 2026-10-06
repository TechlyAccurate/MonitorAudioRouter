"use strict";

const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const repositoryRoot = path.resolve(__dirname, "..");
const chromiumUserAgent = "Mozilla/5.0 Chrome/140.0";
const firefoxUserAgent = "Mozilla/5.0 Firefox/140.0";
const tests = [];
let nextVmSourceId = 1;

function addTest(name, check) {
  tests.push({ name, check });
}

function assertEqual(expected, actual, message) {
  if (expected !== actual) {
    throw new Error(`${message} Expected: ${expected}; actual: ${actual}.`);
  }
}

function assertTrue(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

function loadManifest(browserDirectory) {
  const manifestPath = path.join(repositoryRoot, "extensions", browserDirectory, "manifest.json");
  return JSON.parse(fs.readFileSync(manifestPath, "utf8"));
}

function createEventStub() {
  const listeners = [];
  return {
    addListener(listener) {
      assertEqual("function", typeof listener, "Event listeners must be functions.");
      listeners.push(listener);
    },
    emit(...argumentsToListener) {
      for (const listener of listeners) {
        listener(...argumentsToListener);
      }
    }
  };
}

function createNativePort(postedMessages) {
  return {
    onDisconnect: createEventStub(),
    postMessage(message) {
      postedMessages.push(message);
    }
  };
}

function createTimerStubs() {
  let nextTimerId = 1;
  const intervals = [];
  const timeouts = [];
  return {
    intervals,
    timeouts,
    setInterval(callback, delay) {
      assertEqual("function", typeof callback, "Interval callbacks must be functions.");
      intervals.push({ callback, delay });
      return nextTimerId++;
    },
    setTimeout(callback, delay) {
      assertEqual("function", typeof callback, "Timeout callbacks must be functions.");
      timeouts.push({ callback, delay });
      return nextTimerId++;
    }
  };
}

function createChromiumApi(postedMessages, queryTabs = () => Promise.resolve([])) {
  return {
    action: { onClicked: createEventStub() },
    permissions: {
      request(_permissions, callback) {
        callback(true);
      }
    },
    runtime: {
      connectNative() {
        return createNativePort(postedMessages);
      },
      lastError: null
    },
    tabs: {
      onActivated: createEventStub(),
      onAttached: createEventStub(),
      onDetached: createEventStub(),
      onRemoved: createEventStub(),
      onUpdated: createEventStub(),
      query: queryTabs
    },
    windows: {
      get() {
        return Promise.reject(new Error("No windows are present in the smoke fixture."));
      },
      onBoundsChanged: createEventStub(),
      onRemoved: createEventStub()
    }
  };
}

function createFirefoxApi(postedMessages, queryTabs = () => Promise.resolve([])) {
  return {
    browserAction: { onClicked: createEventStub() },
    runtime: {
      connectNative() {
        return createNativePort(postedMessages);
      }
    },
    tabs: {
      onActivated: createEventStub(),
      onAttached: createEventStub(),
      onDetached: createEventStub(),
      onRemoved: createEventStub(),
      onUpdated: createEventStub(),
      query: queryTabs
    },
    windows: {
      get() {
        return Promise.reject(new Error("No windows are present in the smoke fixture."));
      },
      onRemoved: createEventStub()
    }
  };
}

function createControlledTabQuery() {
  const pendingQueries = [];
  let activeQueries = 0;
  let maximumActiveQueries = 0;

  return {
    get maximumActiveQueries() {
      return maximumActiveQueries;
    },
    get pendingCount() {
      return pendingQueries.length;
    },
    query() {
      activeQueries++;
      maximumActiveQueries = Math.max(maximumActiveQueries, activeQueries);
      return new Promise((resolve) => {
        pendingQueries.push((tabs) => {
          activeQueries--;
          resolve(tabs);
        });
      });
    },
    resolveNext(tabs = []) {
      assertTrue(pendingQueries.length > 0, "A controlled tab query should be pending.");
      pendingQueries.shift()(tabs);
    }
  };
}

function flushAsyncWork() {
  return new Promise((resolve) => setImmediate(resolve));
}

async function evaluateBackgroundScript(browserDirectory, browserApiName, browserApi, userAgent) {
  const backgroundPath = path.join(repositoryRoot, "extensions", browserDirectory, "background.js");
  return evaluateBackgroundFile(backgroundPath, browserDirectory, browserApiName, browserApi, userAgent);
}

async function evaluateBackgroundFile(backgroundPath, sourceName, browserApiName, browserApi, userAgent) {
  const timers = createTimerStubs();
  const sourceId = `${sourceName}-vm-${nextVmSourceId++}`;
  const context = {
    [browserApiName]: browserApi,
    console,
    crypto: { randomUUID: () => sourceId },
    navigator: { userAgent },
    setInterval: timers.setInterval,
    setTimeout: timers.setTimeout
  };

  vm.createContext(context);
  vm.runInContext(fs.readFileSync(backgroundPath, "utf8"), context, { filename: backgroundPath });
  await flushAsyncWork();
  return { timers, userAgent: context.navigator.userAgent };
}

addTest("Chromium manifest parses as JSON", () => {
  const manifest = loadManifest("chromium");
  assertEqual(3, manifest.manifest_version, "The Chromium manifest version should remain valid.");
  assertEqual("background.js", manifest.background.service_worker, "The Chromium background entry point should be declared.");
});

addTest("Firefox manifest parses as JSON", () => {
  const manifest = loadManifest("firefox");
  assertEqual(2, manifest.manifest_version, "The Firefox manifest version should remain valid.");
  assertTrue(manifest.background.scripts.includes("background.js"), "The Firefox background entry point should be declared.");
});

addTest("Chromium background evaluates with stub browser APIs", async () => {
  const postedMessages = [];
  await evaluateBackgroundScript("chromium", "chrome", createChromiumApi(postedMessages), chromiumUserAgent);
  assertEqual(1, postedMessages.length, "Chromium startup should send one initial snapshot.");
  assertEqual("audibleWindows", postedMessages[0].type, "Chromium should send the expected message type.");
});

addTest("Firefox background evaluates with stub browser APIs", async () => {
  const postedMessages = [];
  await evaluateBackgroundScript("firefox", "browser", createFirefoxApi(postedMessages), firefoxUserAgent);
  assertEqual(1, postedMessages.length, "Firefox startup should send one initial snapshot.");
  assertEqual("audibleWindows", postedMessages[0].type, "Firefox should send the expected message type.");
});

addTest("Generated store backgrounds evaluate with stub browser APIs", async () => {
  const fixtures = [
    {
      name: "packaged-chromium",
      path: path.join(repositoryRoot, "packages", "browser-store", "chrome", "unpacked", "background.js"),
      apiName: "chrome",
      api: createChromiumApi([]),
      userAgent: chromiumUserAgent
    },
    {
      name: "packaged-firefox",
      path: path.join(repositoryRoot, "packages", "browser-store", "firefox", "unpacked", "background.js"),
      apiName: "browser",
      api: createFirefoxApi([]),
      userAgent: firefoxUserAgent
    }
  ];

  for (const fixture of fixtures) {
    assertTrue(fs.existsSync(fixture.path), `${fixture.name} should exist after the package build.`);
    await evaluateBackgroundFile(
      fixture.path,
      fixture.name,
      fixture.apiName,
      fixture.api,
      fixture.userAgent);
  }
});

addTest("Firefox VM uses a Firefox user agent", async () => {
  const evaluation = await evaluateBackgroundScript("firefox", "browser", createFirefoxApi([]), firefoxUserAgent);
  const userAgent = evaluation.userAgent;
  assertEqual("string", typeof userAgent, "The evaluator should report its VM user agent.");
  assertTrue(userAgent.includes("Firefox/"), "The Firefox VM should expose a Firefox user agent.");
});

addTest("Browser snapshot bursts coalesce and sequence messages in send order", async () => {
  const fixtures = [
    { directory: "chromium", apiName: "chrome", userAgent: chromiumUserAgent, createApi: createChromiumApi },
    { directory: "firefox", apiName: "browser", userAgent: firefoxUserAgent, createApi: createFirefoxApi }
  ];

  for (const fixture of fixtures) {
    const postedMessages = [];
    const controlledQuery = createControlledTabQuery();
    const browserApi = fixture.createApi(postedMessages, controlledQuery.query);
    const evaluation = await evaluateBackgroundScript(
      fixture.directory,
      fixture.apiName,
      browserApi,
      fixture.userAgent);

    assertEqual(1, controlledQuery.pendingCount, `${fixture.directory} should start one collector.`);
    browserApi.tabs.onUpdated.emit(1, {}, {});
    browserApi.tabs.onUpdated.emit(1, {}, {});
    assertEqual(1, controlledQuery.maximumActiveQueries, `${fixture.directory} collectors must not overlap.`);

    controlledQuery.resolveNext();
    await flushAsyncWork();
    assertEqual(1, controlledQuery.pendingCount, `${fixture.directory} should coalesce the burst into one follow-up collector.`);
    controlledQuery.resolveNext();
    await flushAsyncWork();

    assertEqual(2, postedMessages.length, `${fixture.directory} should send the initial and coalesced snapshots.`);
    assertEqual(1, postedMessages[0].sequence, `${fixture.directory} should start its source sequence at one.`);
    assertEqual(2, postedMessages[1].sequence, `${fixture.directory} should increment sequence in send order.`);
    assertEqual(
      postedMessages[0].sourceInstanceId,
      postedMessages[1].sourceInstanceId,
      `${fixture.directory} should keep one source ID for the worker lifetime.`);

    if (fixture.directory === "firefox") {
      const burstDelays = evaluation.timers.timeouts.map((timer) => timer.delay);
      assertTrue(burstDelays.includes(150), "Firefox should preserve its 150 ms burst retry.");
      assertTrue(burstDelays.includes(750), "Firefox should preserve its 750 ms burst retry.");
    }
  }
});

addTest("Restarted browser sources begin again at sequence one", async () => {
  const firstMessages = [];
  const secondMessages = [];

  await evaluateBackgroundScript("chromium", "chrome", createChromiumApi(firstMessages), chromiumUserAgent);
  await evaluateBackgroundScript("chromium", "chrome", createChromiumApi(secondMessages), chromiumUserAgent);

  assertEqual(1, firstMessages[0].sequence, "The first worker should begin at sequence one.");
  assertEqual(1, secondMessages[0].sequence, "A restarted worker should begin at sequence one.");
  assertTrue(
    firstMessages[0].sourceInstanceId !== secondMessages[0].sourceInstanceId,
    "A restarted worker must receive a new source instance ID.");
});

async function runTests() {
  let passed = 0;
  for (const test of tests) {
    try {
      await test.check();
      passed++;
      console.log(`[PASS] ${test.name}`);
    } catch (error) {
      console.error(`[FAIL] ${test.name}: ${error.message}`);
    }
  }

  console.log(`Browser extension regression tests passed: ${passed}/${tests.length}.`);
  if (passed !== tests.length) {
    process.exitCode = 1;
  }
}

runTests().catch((error) => {
  console.error(`[FAIL] Browser extension regression runner: ${error.stack || error.message}`);
  process.exitCode = 1;
});
