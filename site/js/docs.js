// The docs page.
// Marks the section being read in the sidebar: the last heading that has scrolled past the top third of the window.
(() => {
  const links = [...document.querySelectorAll('.toc a[href^="#"]')];
  const sections = links.map(a => document.getElementById(a.hash.slice(1)));
  let queued = false;
  function mark() {
    queued = false;
    const line = innerHeight / 3;
    let current = 0;
    sections.forEach((s, i) => { if (s.getBoundingClientRect().top <= line) current = i; });
    if (innerHeight + scrollY >= document.documentElement.scrollHeight - 4) current = sections.length - 1; // at the very end
    links.forEach((a, i) => a.toggleAttribute('aria-current', i === current));
  }
  addEventListener('scroll', () => { if (!queued) { queued = true; requestAnimationFrame(mark); } }, { passive: true });
  addEventListener('resize', mark);
  mark();
})();
