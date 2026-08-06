'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const experienceSource = fs.readFileSync(
  path.join(__dirname, '..', 'docs', 'experience.js'),
  'utf8'
);
const stylesSource = fs.readFileSync(
  path.join(__dirname, '..', 'docs', 'styles.css'),
  'utf8'
);

function cssVariable(name) {
  const match = stylesSource.match(new RegExp(`--${name}:\\s*(#[0-9a-f]{6})`, 'i'));
  assert.ok(match, `expected --${name} to be a six-digit hex color`);
  return match[1];
}

function relativeLuminance(hex) {
  const value = hex.slice(1);
  const channels = [0, 2, 4].map((index) => parseInt(value.slice(index, index + 2), 16) / 255);
  const linear = channels.map((channel) => (
    channel <= 0.04045
      ? channel / 12.92
      : Math.pow((channel + 0.055) / 1.055, 2.4)
  ));
  return 0.2126 * linear[0] + 0.7152 * linear[1] + 0.0722 * linear[2];
}

function contrastRatio(foreground, background) {
  const values = [relativeLuminance(foreground), relativeLuminance(background)].sort((a, b) => b - a);
  return (values[0] + 0.05) / (values[1] + 0.05);
}

function createClassList() {
  const values = new Set();
  return {
    add(...names) {
      names.forEach((name) => values.add(name));
    },
    contains(name) {
      return values.has(name);
    },
    toggle(name, force) {
      const enabled = force === undefined ? !values.has(name) : Boolean(force);
      if (enabled) values.add(name);
      else values.delete(name);
      return enabled;
    }
  };
}

function createHarness({ reducedMotion = false } = {}) {
  const callbacks = new Map();
  const documentListeners = new Map();
  const mediaListeners = new Map();
  const rootClassList = createClassList();
  const motionClassList = createClassList();
  const progressValues = new Map();
  const value = { textContent: '100' };
  const progress = {
    style: {
      setProperty(name, nextValue) {
        progressValues.set(name, nextValue);
      }
    }
  };
  const motionRoot = { classList: motionClassList };
  let clock = 0;
  let nextFrameId = 1;

  const document = {
    hidden: false,
    documentElement: { classList: rootClassList },
    addEventListener(name, listener) {
      documentListeners.set(name, listener);
    },
    querySelector(selector) {
      if (selector === '[data-count]') return value;
      if (selector === '[data-progress]') return progress;
      if (selector === '[data-motion-root]') return motionRoot;
      return null;
    }
  };

  const mediaQuery = {
    matches: reducedMotion,
    addEventListener(name, listener) {
      mediaListeners.set(name, listener);
    }
  };

  const window = {
    cancelAnimationFrame(frameId) {
      callbacks.delete(frameId);
    },
    matchMedia() {
      return mediaQuery;
    },
    requestAnimationFrame(callback) {
      const frameId = nextFrameId;
      nextFrameId += 1;
      callbacks.set(frameId, callback);
      return frameId;
    }
  };

  vm.runInNewContext(
    experienceSource,
    { document, performance: { now: () => clock }, window },
    { filename: 'docs/experience.js' }
  );

  return {
    callbacks,
    document,
    motionClassList,
    progressValues,
    rootClassList,
    value,
    dispatchVisibility(hidden) {
      document.hidden = hidden;
      documentListeners.get('visibilitychange')();
    },
    runNextFrame(timestamp) {
      clock = timestamp;
      const next = callbacks.entries().next();
      assert.equal(next.done, false, 'expected a scheduled animation frame');
      const [frameId, callback] = next.value;
      callbacks.delete(frameId);
      callback(timestamp);
    }
  };
}

test('animates the illustrative allowance from 38 to 100 once', () => {
  const harness = createHarness();

  assert.equal(harness.value.textContent, '38');
  assert.equal(harness.progressValues.get('--progress'), '0.38');

  harness.runNextFrame(0);
  harness.runNextFrame(825);
  assert.equal(harness.value.textContent, '92');

  harness.runNextFrame(1650);
  assert.equal(harness.value.textContent, '100');
  assert.equal(harness.progressValues.get('--progress'), '1');
  assert.equal(harness.rootClassList.contains('entrance-complete'), true);
  assert.equal(harness.callbacks.size, 0);
});

test('shows the final state immediately when reduced motion is requested', () => {
  const harness = createHarness({ reducedMotion: true });

  assert.equal(harness.value.textContent, '100');
  assert.equal(harness.progressValues.get('--progress'), '1');
  assert.equal(harness.rootClassList.contains('is-visible'), true);
  assert.equal(harness.rootClassList.contains('entrance-complete'), true);
  assert.equal(harness.callbacks.size, 0);
});

test('pauses visual motion and allowance progress while the page is hidden', () => {
  const harness = createHarness();

  harness.runNextFrame(0);
  harness.dispatchVisibility(true);
  assert.equal(harness.motionClassList.contains('is-paused'), true);
  assert.equal(harness.callbacks.size, 0);

  harness.dispatchVisibility(false);
  assert.equal(harness.motionClassList.contains('is-paused'), false);
  harness.runNextFrame(1000);
  harness.runNextFrame(1825);

  assert.equal(harness.value.textContent, '92');
});

test('text tokens meet WCAG AA contrast on their light surfaces', () => {
  const canvas = cssVariable('canvas');
  const surface = cssVariable('surface');

  for (const [name, foreground, background] of [
    ['ink', cssVariable('ink'), canvas],
    ['ink-soft', cssVariable('ink-soft'), canvas],
    ['muted', cssVariable('muted'), surface],
    ['accent-deep', cssVariable('accent-deep'), surface]
  ]) {
    assert.ok(
      contrastRatio(foreground, background) >= 4.5,
      `${name} must maintain at least 4.5:1 contrast`
    );
  }
});

test('the primary action maintains WCAG AA text contrast', () => {
  assert.ok(
    contrastRatio('#ffffff', cssVariable('accent')) >= 4.5,
    'white button text must maintain at least 4.5:1 contrast'
  );
});
