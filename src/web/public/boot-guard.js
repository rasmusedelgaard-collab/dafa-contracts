// If the app's script cannot run at all (a browser too old to read it, a script file that did not
// load after a deployment), the page would stay blank. This plain ES5 script, separate from the
// app bundle and from theme-init.js, puts a readable message in the empty page instead. It does
// nothing once the app has drawn anything. Not inline, so a strict Content-Security-Policy
// (script-src 'self') still allows it.
(function () {
  function show(detail) {
    try {
      var root = document.getElementById('root');
      if (!root || root.firstChild) return;
      var box = document.createElement('div');
      box.setAttribute('role', 'alert');
      box.style.cssText = 'max-width:460px;margin:15vh auto;padding:24px;font:16px/1.5 -apple-system,system-ui,sans-serif';
      var title = document.createElement('h1');
      title.style.cssText = 'font-size:20px;margin:0 0 8px';
      title.appendChild(document.createTextNode('DAFA Contracts could not start in this browser'));
      var text = document.createElement('p');
      text.appendChild(document.createTextNode('Reload the page. If it keeps happening, update the browser or try another one, and tell us this message: ' + detail));
      box.appendChild(title);
      box.appendChild(text);
      root.appendChild(box);
    } catch (e) {
      /* nothing more can be done */
    }
  }
  try {
    window.addEventListener('error', function (event) {
      var target = event && event.target;
      // A script or stylesheet of the page itself that failed to load (the event does not bubble: capture).
      if (target && target !== window && target.tagName === 'SCRIPT') show('a script file could not be loaded (' + (target.src || '').replace(/^.*\//, '') + ')');
      else if (event && event.message) show(String(event.message));
    }, true);
    window.addEventListener('unhandledrejection', function (event) {
      var reason = event && event.reason;
      show(String((reason && reason.message) || reason || 'unknown error'));
    });
  } catch (e) {
    /* a browser without addEventListener cannot run the app either */
  }
})();
