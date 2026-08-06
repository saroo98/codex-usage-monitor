(() => {
  'use strict';

  const root = document.documentElement;
  const value = document.querySelector('[data-count]');
  const progress = document.querySelector('[data-progress]');
  const motionRoot = document.querySelector('[data-motion-root]');
  const hourglass = document.querySelector('[data-hourglass]');
  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  const startValue = 38;
  const endValue = 100;
  const duration = 1650;
  let frame = 0;
  let stepTimer = 0;
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
    if (stepTimer) window.clearTimeout(stepTimer);
    frame = 0;
    stepTimer = 0;
    complete = true;
    render(endValue);
    root.classList.add('is-visible', 'entrance-complete');
  };

  const restartSequence = (flip = false) => {
    if (frame) window.cancelAnimationFrame(frame);
    if (stepTimer) window.clearTimeout(stepTimer);
    frame = 0;
    stepTimer = 0;
    complete = false;
    previousFrame = null;
    elapsed = 0;
    render(startValue);
    root.classList.remove('entrance-complete');
    root.classList.add('is-visible');

    if (hourglass) {
      if (flip) hourglass.classList.toggle('is-flipped');
      hourglass.classList.remove('is-replaying');
      void hourglass.offsetWidth;
      hourglass.classList.add('is-replaying');
    }

    if (reducedMotion.matches) stepTimer = window.setTimeout(finish, 250);
    else syncVisibility();
  };

  const resetTilt = () => {
    if (!hourglass) return;
    hourglass.style.setProperty('--tilt-x', '0deg');
    hourglass.style.setProperty('--tilt-y', '0deg');
  };

  const updateTilt = (event) => {
    if (!hourglass || reducedMotion.matches) {
      resetTilt();
      return;
    }

    const bounds = hourglass.getBoundingClientRect();
    const horizontal = Math.max(-1, Math.min(1, ((event.clientX - bounds.left) / bounds.width) * 2 - 1));
    const vertical = Math.max(-1, Math.min(1, ((event.clientY - bounds.top) / bounds.height) * 2 - 1));
    hourglass.style.setProperty('--tilt-x', `${Number((-vertical * 3).toFixed(2))}deg`);
    hourglass.style.setProperty('--tilt-y', `${Number((horizontal * 3).toFixed(2))}deg`);
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
  if (hourglass) {
    resetTilt();
    hourglass.addEventListener('click', () => restartSequence(true));
    hourglass.addEventListener('pointermove', updateTilt);
    hourglass.addEventListener('pointerleave', resetTilt);
  }
  reducedMotion.addEventListener('change', (event) => {
    if (event.matches) resetTilt();
    if (event.matches && value && progress) restartSequence();
  });

  if (!value || !progress) {
    root.classList.add('is-visible');
    syncVisibility();
    return;
  }

  restartSequence();
})();
