(function () {
  // Botão copiar em cada bloco de código
  document.querySelectorAll('pre').forEach(function (pre) {
    var b = document.createElement('button');
    b.className = 'copy';
    b.type = 'button';
    b.textContent = 'Copiar';
    b.addEventListener('click', function () {
      var text = pre.querySelector('code').innerText;
      var done = function () { b.textContent = 'Copiado!'; setTimeout(function () { b.textContent = 'Copiar'; }, 1500); };
      if (navigator.clipboard) navigator.clipboard.writeText(text).then(done);
    });
    pre.appendChild(b);
  });

  // Abas dos cenários
  var tabs = document.querySelectorAll('.tabs button');
  function show(id) {
    tabs.forEach(function (t) { t.setAttribute('aria-selected', String(t.dataset.target === id)); });
    document.querySelectorAll('.panel').forEach(function (p) { p.classList.toggle('on', p.id === id); });
  }
  tabs.forEach(function (t) {
    t.addEventListener('click', function () {
      show(t.dataset.target);
      try { history.replaceState(null, '', '#' + t.dataset.target); } catch (e) {}
    });
  });
  document.querySelectorAll('a[data-scenario]').forEach(function (a) {
    a.addEventListener('click', function () { show(a.dataset.scenario); });
  });
  var h = location.hash.slice(1);
  if (h && document.getElementById(h) && document.getElementById(h).classList.contains('panel')) show(h);

  // Abas por sistema operacional (Windows / Linux-macOS): a escolha vale para todas e fica lembrada
  var osButtons = document.querySelectorAll('[data-os-tabs] [role="tab"]');
  function showOs(os) {
    osButtons.forEach(function (b) { b.setAttribute('aria-selected', String(b.dataset.os === os)); });
    document.querySelectorAll('[data-os-tabs] .ospanel').forEach(function (p) { p.classList.toggle('on', p.dataset.os === os); });
  }
  osButtons.forEach(function (b) {
    b.addEventListener('click', function () {
      showOs(b.dataset.os);
      try { localStorage.setItem('os', b.dataset.os); } catch (e) {}
    });
  });
  if (osButtons.length) {
    var os = null;
    try { os = localStorage.getItem('os'); } catch (e) {}
    showOs(os || (/Windows/i.test(navigator.userAgent) ? 'windows' : 'linux'));
  }

  // Destaque do item ativo no menu
  var links = {};
  document.querySelectorAll('nav.side a').forEach(function (a) { links[a.getAttribute('href').slice(1)] = a; });
  var io = new IntersectionObserver(function (entries) {
    entries.forEach(function (e) {
      if (e.isIntersecting && links[e.target.id]) {
        Object.values(links).forEach(function (l) { l.classList.remove('active'); });
        links[e.target.id].classList.add('active');
      }
    });
  }, { rootMargin: '-80px 0px -70% 0px' });
  document.querySelectorAll('main h2[id]').forEach(function (s) { io.observe(s); });

  // Tema
  var root = document.documentElement, btn = document.getElementById('theme');
  try { var saved = localStorage.getItem('theme'); if (saved) root.dataset.theme = saved; } catch (e) {}
  btn.addEventListener('click', function () {
    var dark = root.dataset.theme ? root.dataset.theme === 'dark' : matchMedia('(prefers-color-scheme: dark)').matches;
    root.dataset.theme = dark ? 'light' : 'dark';
    try { localStorage.setItem('theme', root.dataset.theme); } catch (e) {}
  });
})();
