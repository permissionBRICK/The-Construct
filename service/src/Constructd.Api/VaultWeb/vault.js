// The key vault's phone pages (docs/plans/key-vault-hosted.md). Served by the host service under a
// strict CSP: no inline code, no third-party resources, and every piece of data reaches the page
// through textContent only.
(function () {
  'use strict';

  var KEY = 'constructVaultDevice';
  var REFRESH_MS = 3000;
  var APPROVE_DELAY_MS = 1000;
  // #request=<id>: opened from T3 Code's "waiting for approval" banner; that card is highlighted.
  var focusId = null;

  function $(id) { return document.getElementById(id); }

  function readToken() {
    try { return localStorage.getItem(KEY); } catch (e) { return null; }
  }

  function forgetToken() {
    try { localStorage.removeItem(KEY); } catch (e) { /* nothing stored */ }
  }

  function status(text, error) {
    var node = $('status');
    if (!node) return;
    node.textContent = text || '';
    node.classList.toggle('error', !!error);
  }

  // Same-origin call to the device routes; the pages live one level below the origin's root.
  function api(path, method, body) {
    var token = readToken() || '';
    var init = {
      method: method || 'GET',
      headers: { 'Authorization': 'VaultDevice ' + token, 'Accept': 'application/json' },
      credentials: 'omit',
      cache: 'no-store',
      referrerPolicy: 'no-referrer',
      redirect: 'error'
    };
    if (body !== undefined) {
      init.headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(body);
    }
    return fetch(new URL('../api/v1/' + path, location.href).href, init);
  }

  function el(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = String(text);
    return node;
  }

  function time(ms) {
    try { return new Date(ms).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }); } catch (e) { return ''; }
  }

  function size(bytes) {
    if (!bytes) return '';
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1048576) return (bytes / 1024).toFixed(1) + ' KB';
    return (bytes / 1048576).toFixed(1) + ' MB';
  }

  // ── pair.html ──────────────────────────────────────────────────────────────

  // T3 Code is served from inside the VM: on this page's own origin its scripts could read the
  // device token, so a link there is never followed (the Companion refuses such a code as well).
  function safeNext(next) {
    if (!next) return null;
    try {
      var url = new URL(next);
      if (url.origin === location.origin) return null;
      return url.protocol === 'https:' || url.protocol === 'http:' ? url.href : null;
    } catch (e) {
      return null;
    }
  }

  function pair() {
    var params = new URLSearchParams(location.hash.replace(/^#/, ''));
    var token = params.get('token');
    var next = params.get('next');
    // Both tokens leave the address bar (and the history entry) at once.
    if (location.hash) history.replaceState(null, '', location.pathname + location.search);
    if (token) {
      try { localStorage.setItem(KEY, token); } catch (e) {
        status('This browser does not let the page store the pairing (private browsing?). Open the link in a normal tab.', true);
        return;
      }
    }
    if (!readToken()) {
      status('No pairing found. Scan the QR code from the Construct Companion: Key Vault → Hosts → Pair a phone.', true);
      return;
    }
    api('vault/device').then(function (response) {
      if (response.status === 401) {
        forgetToken();
        status('This pairing is not valid any more (expired or revoked). Pair the phone again from the Construct Companion.', true);
        return null;
      }
      if (!response.ok) {
        status('The host answered with an error (' + response.status + '). Try again later.', true);
        return null;
      }
      return response.json();
    }).then(function (me) {
      if (!me) return;
      var label = me.label || 'this device';
      var target = safeNext(next);
      if (target) {
        status('Paired as ' + label + '. Opening T3 Code…');
        location.replace(target);
        return;
      }
      status(next ? 'Paired as ' + label + '. The T3 Code link was not opened: it must use a different address than this page.' : 'Paired as ' + label + '.', !!next);
      $('done').hidden = false;
    }).catch(function () {
      status('Cannot reach the host. Check the connection and open the pairing link again.', true);
    });
  }

  // ── index.html ─────────────────────────────────────────────────────────────

  var timer = null;
  var stopped = false;

  function unpaired() {
    stopped = true;
    if (timer) clearInterval(timer);
    $('approvals').replaceChildren();
    $('files').replaceChildren();
    $('approvals-empty').hidden = true;
    $('files-empty').hidden = true;
    $('who').textContent = '';
    status('This phone is not paired, or its pairing was revoked. Pair it again: in the Construct Companion open Key Vault → Hosts → Pair a phone and scan the QR code.', true);
  }

  // Keeps cards that are still pending (their timers and focus) and adds or removes the rest.
  function sync(list, items, build) {
    var seen = {};
    items.forEach(function (item) {
      seen[item.id] = true;
      if (!list.querySelector('[data-id="' + CSS.escape(item.id) + '"]')) list.appendChild(build(item));
    });
    Array.prototype.slice.call(list.children).forEach(function (card) {
      if (!seen[card.getAttribute('data-id')]) card.remove();
    });
  }

  function answer(card, path, body, done) {
    var buttons = card.querySelectorAll('button');
    buttons.forEach(function (b) { b.disabled = true; });
    api(path, 'POST', body).then(function (response) {
      if (response.status === 401) { unpaired(); return; }
      if (response.status === 204) { card.remove(); status(done); refresh(); return; }
      if (response.status === 409) { card.remove(); status('That request was already answered or timed out.'); refresh(); return; }
      if (response.status === 404) { card.remove(); status('That item is no longer pending.'); refresh(); return; }
      status('The host answered with an error (' + response.status + ').', true);
      buttons.forEach(function (b) { b.disabled = false; });
    }).catch(function () {
      status('Cannot reach the host.', true);
      buttons.forEach(function (b) { b.disabled = false; });
    });
  }

  function approvalCard(item) {
    var card = el('li', 'card');
    card.setAttribute('data-id', item.id);
    if (item.id === focusId) {
      card.classList.add('focus');
      focusId = null;
      setTimeout(function () { card.scrollIntoView({ block: 'center' }); }, 0);
    }
    card.appendChild(el('h3', null, item.title));
    var meta = 'VM ' + item.vm + (item.source ? ' · ' + item.source : '') + ' · answer by ' + time(item.deadline);
    card.appendChild(el('p', 'meta', meta));
    card.appendChild(el('p', 'message', item.message));
    var actions = el('div', 'actions');
    var deny = el('button', null, 'Deny');
    deny.type = 'button';
    var approve = el('button', 'primary', item.action || 'Approve');
    approve.type = 'button';
    // Like the Companion dialog: approve becomes clickable after a second, so a stray tap cannot approve.
    approve.disabled = true;
    setTimeout(function () { if (!card.classList.contains('busy')) approve.disabled = false; }, APPROVE_DELAY_MS);
    deny.addEventListener('click', function () {
      card.classList.add('busy');
      answer(card, 'vault/approvals/' + encodeURIComponent(item.id), { decision: 'deny' }, 'Denied.');
    });
    approve.addEventListener('click', function () {
      card.classList.add('busy');
      answer(card, 'vault/approvals/' + encodeURIComponent(item.id), { decision: 'approve' }, 'Approved.');
    });
    actions.appendChild(deny);
    actions.appendChild(approve);
    card.appendChild(actions);
    return card;
  }

  function fileCard(item) {
    var card = el('li', 'card');
    card.setAttribute('data-id', item.id);
    card.appendChild(el('p', 'path', item.path));
    var kind = item.type === 'sqlite' ? 'SQLite database' : item.type === 'binary' ? 'binary file' : 'text file';
    var meta = 'VM ' + item.vm + ' · ' + (item.names || []).join(', ') + ' · ' + kind + (item.size ? ' · ' + size(item.size) : '');
    card.appendChild(el('p', 'meta', meta));
    var actions = el('div', 'actions');
    [['keep', 'Keep', null, 'Kept.'], ['redact', 'Redact', 'primary', 'The file will be redacted.'],
      ['delete', 'Delete file', 'danger', 'The file will be deleted.']].forEach(function (spec) {
      var button = el('button', spec[2], spec[1]);
      button.type = 'button';
      button.addEventListener('click', function () {
        if (spec[0] === 'delete' && !confirm('Delete ' + item.path + ' on the VM “' + item.vm + '”?')) return;
        answer(card, 'vault/files/' + encodeURIComponent(item.id), { action: spec[0] }, spec[3]);
      });
      actions.appendChild(button);
    });
    card.appendChild(actions);
    if (item.type === 'sqlite') card.appendChild(el('p', 'note', 'Deleting a database also deletes its -wal, -shm and -journal files.'));
    return card;
  }

  function refresh() {
    if (stopped) return;
    Promise.all([api('vault/approvals'), api('vault/files')]).then(function (responses) {
      if (responses[0].status === 401 || responses[1].status === 401) { unpaired(); return null; }
      if (!responses[0].ok || !responses[1].ok) {
        status('The host answered with an error (' + (responses[0].ok ? responses[1].status : responses[0].status) + ').', true);
        return null;
      }
      return Promise.all([responses[0].json(), responses[1].json()]);
    }).then(function (data) {
      if (!data || stopped) return;
      var approvals = data[0].approvals || [];
      var files = data[1].files || [];
      sync($('approvals'), approvals, approvalCard);
      sync($('files'), files, fileCard);
      $('approvals-empty').hidden = approvals.length > 0;
      $('files-empty').hidden = files.length > 0;
      var node = $('status');
      if (node.classList.contains('error')) status('');
    }).catch(function () {
      if (!stopped) status('Cannot reach the host; retrying…', true);
    });
  }

  function index() {
    focusId = new URLSearchParams(location.hash.replace(/^#/, '')).get('request');
    if (location.hash) history.replaceState(null, '', location.pathname + location.search);
    if (!readToken()) { unpaired(); return; }
    api('vault/device').then(function (response) {
      if (response.status === 401) { unpaired(); return null; }
      return response.ok ? response.json() : null;
    }).then(function (me) {
      if (me) $('who').textContent = 'Answering for ' + me.user + ' on “' + me.label + '”.';
    }).catch(function () { /* the refresh reports reachability */ });
    refresh();
    timer = setInterval(refresh, REFRESH_MS);
    document.addEventListener('visibilitychange', function () { if (!document.hidden) refresh(); });
  }

  document.addEventListener('DOMContentLoaded', function () {
    var page = document.body.getAttribute('data-page');
    if (page === 'pair') pair();
    else if (page === 'index') index();
  });
})();
