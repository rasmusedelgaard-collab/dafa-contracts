// Applies the saved theme before the first paint (no flash of the light theme). A plain script,
// not inline, so a strict Content-Security-Policy (script-src 'self') still allows it.
(function () {
  try {
    var pref = localStorage.getItem('dafa.theme') || 'light';
    var dark = pref === 'dark' || (pref === 'system' && window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
    document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
  } catch (e) {
    document.documentElement.setAttribute('data-theme', 'light');
  }
})();
