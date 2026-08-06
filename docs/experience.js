(() => {
  'use strict';

  const root = document.documentElement;
  const value = document.querySelector('[data-count]');
  const progress = document.querySelector('[data-progress]');
  const motionRoot = document.querySelector('[data-motion-root]');
  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  const startValue = 38;
  const endValue = 100;
  const duration = 1650;
  let frame = 0;
  let previousFrame = null;
  let elapsed = 0;
  let complete = false;

  const render = (number) => {
    const rounded = Math.round(number);
    value.textContent = String(rounded);
    progress.style.setProperty('--progress', String(number / endValue));
  };

  const finish = () => {
    if (frame) window.cancelAnimationFrame(frame);
    frame = 0;
    complete = true;
    render(endValue);
    root.classList.add('is-visible', 'entrance-complete');
  };

  const tick = (timestamp) => {
    frame = 0;
    root.classList.add('is-visible');
    if (document.hidden || complete) return;

    if (previousFrame !== null) elapsed += timestamp - previousFrame;
    previousFrame = timestamp;
    const ratio = Math.min(1, elapsed / duration);
    const eased = 1 - Math.pow(1 - ratio, 3);
    render(startValue + (endValue - startValue) * eased);

    if (ratio >= 1) finish();
    else frame = window.requestAnimationFrame(tick);
  };

  const syncVisibility = () => {
    const hidden = document.hidden;
    if (motionRoot) motionRoot.classList.toggle('is-paused', hidden);
    if (complete || reducedMotion.matches) return;

    if (hidden) {
      if (frame) window.cancelAnimationFrame(frame);
      frame = 0;
      previousFrame = null;
    } else if (!frame) {
      previousFrame = null;
      frame = window.requestAnimationFrame(tick);
    }
  };

  root.classList.add('is-enhanced');
  document.addEventListener('visibilitychange', syncVisibility);
  reducedMotion.addEventListener('change', (event) => {
    if (event.matches && value && progress) finish();
  });

  if (!value || !progress) {
    root.classList.add('is-visible');
    syncVisibility();
    return;
  }

  if (reducedMotion.matches) {
    finish();
    syncVisibility();
    return;
  }

  render(startValue);
  syncVisibility();
})();
