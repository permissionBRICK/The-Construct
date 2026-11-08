(() => {
  const paths = {
    home: '<path d="M3 11.5 12 4l9 7.5"/><path d="M5.5 10v10h13V10M9 20v-6h6v6"/>',
    server: '<rect x="3" y="3" width="18" height="7" rx="2"/><rect x="3" y="14" width="18" height="7" rx="2"/><path d="M7 6.5h.01M7 17.5h.01M11 6.5h6M11 17.5h6"/>',
    folder: '<path d="M3 6.5h6l2 2h10v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/><path d="M3 6.5v-1a2 2 0 0 1 2-2h4l2 3"/>',
    shield: '<path d="M12 3 20 6v5c0 5-3.3 8.2-8 10-4.7-1.8-8-5-8-10V6z"/><path d="m8.5 12 2.2 2.2 4.8-5"/>',
    sliders: '<path d="M4 6h5M15 6h5M4 12h10M18 12h2M4 18h2M12 18h8"/><circle cx="12" cy="6" r="3"/><circle cx="16" cy="12" r="2"/><circle cx="9" cy="18" r="3"/>',
    activity: '<path d="M3 12h4l2.2-6 4.2 12 2.2-6H21"/>',
    search: '<circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 5 5"/>',
    bell: '<path d="M18 9a6 6 0 0 0-12 0c0 7-3 7-3 7h18s-3 0-3-7M10 20h4"/>',
    chevronDown: '<path d="m6 9 6 6 6-6"/>',
    chevronRight: '<path d="m9 6 6 6-6 6"/>',
    arrowUpRight: '<path d="M7 17 17 7M7 7h10v10"/>',
    arrowRight: '<path d="M4 12h16m-6-6 6 6-6 6"/>',
    arrowLeft: '<path d="M20 12H4m6-6-6 6 6 6"/>',
    play: '<path d="m8 5 11 7-11 7z"/>',
    stop: '<rect x="6" y="6" width="12" height="12" rx="2"/>',
    terminal: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="m7 9 3 3-3 3M13 15h4"/>',
    refresh: '<path d="M20 7v5h-5M4 17v-5h5"/><path d="M6.1 8a7 7 0 0 1 11.5-1L20 12M4 12l2.4 5a7 7 0 0 0 11.5-1"/>',
    plus: '<path d="M12 5v14M5 12h14"/>',
    more: '<circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/>',
    check: '<path d="m5 12 4 4L19 6"/>',
    alert: '<path d="M12 3 2.5 20h19z"/><path d="M12 9v4M12 17h.01"/>',
    info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 7h.01"/>',
    clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
    cpu: '<rect x="7" y="7" width="10" height="10" rx="2"/><path d="M9 1v3M15 1v3M9 20v3M15 20v3M20 9h3M20 14h3M1 9h3M1 14h3M4 4h16v16H4z"/>',
    database: '<ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M4 5v7c0 1.7 3.6 3 8 3s8-1.3 8-3V5M4 12v7c0 1.7 3.6 3 8 3s8-1.3 8-3v-7"/>',
    git: '<path d="M6 3v12M6 15a3 3 0 1 0 0 6 3 3 0 0 0 0-6ZM18 3a3 3 0 1 0 0 6 3 3 0 0 0 0-6ZM18 9v2a4 4 0 0 1-4 4H6"/>',
    mic: '<rect x="9" y="3" width="6" height="12" rx="3"/><path d="M5 11a7 7 0 0 0 14 0M12 18v3M8 21h8"/>',
    wifi: '<path d="M3 9a14 14 0 0 1 18 0M6 13a9 9 0 0 1 12 0M9.5 16.5a4 4 0 0 1 5 0M12 20h.01"/>',
    globe: '<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3a15 15 0 0 1 0 18M12 3a15 15 0 0 0 0 18"/>',
    settings: '<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.9l.1.1-2.8 2.8-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.6v.2h-4V21a1.7 1.7 0 0 0-1-1.6 1.7 1.7 0 0 0-1.9.3l-.1.1L4.2 17l.1-.1a1.7 1.7 0 0 0 .3-1.9A1.7 1.7 0 0 0 3 14H2.8v-4H3a1.7 1.7 0 0 0 1.6-1 1.7 1.7 0 0 0-.3-1.9L4.2 7 7 4.2l.1.1a1.7 1.7 0 0 0 1.9.3A1.7 1.7 0 0 0 10 3V2.8h4V3a1.7 1.7 0 0 0 1 1.6 1.7 1.7 0 0 0 1.9-.3l.1-.1L19.8 7l-.1.1a1.7 1.7 0 0 0-.3 1.9 1.7 1.7 0 0 0 1.6 1h.2v4H21a1.7 1.7 0 0 0-1.6 1Z"/>',
    key: '<circle cx="8" cy="15" r="4"/><path d="m11 12 9-9M16 7l2 2M14 9l2 2"/>',
    users: '<path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M22 21v-2a4 4 0 0 0-3-3.7M16 3.2a4 4 0 0 1 0 7.6"/>',
    monitor: '<rect x="2" y="3" width="20" height="14" rx="2"/><path d="M8 21h8M12 17v4"/>',
    copy: '<rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V4H4v12h4"/>',
    trash: '<path d="M4 7h16M9 7V4h6v3M6 7l1 14h10l1-14M10 11v6M14 11v6"/>',
    x: '<path d="m6 6 12 12M18 6 6 18"/>',
    link: '<path d="M10 13a5 5 0 0 0 7.5.5l2-2a5 5 0 0 0-7-7l-1.2 1.2M14 11a5 5 0 0 0-7.5-.5l-2 2a5 5 0 0 0 7 7l1.2-1.2"/>',
    download: '<path d="M12 3v12m-5-5 5 5 5-5M4 20h16"/>',
    upload: '<path d="M12 16V4m-5 5 5-5 5 5M4 20h16"/>',
    menu: '<path d="M4 6h16M4 12h16M4 18h16"/>',
    command: '<path d="M9 6a3 3 0 1 0-3 3h12a3 3 0 1 0-3-3v12a3 3 0 1 0 3-3H6a3 3 0 1 0 3 3z"/>',
    inbox: '<path d="M4 4h16v16H4z"/><path d="M4 14h4l2 3h4l2-3h4"/>',
    layers: '<path d="m12 3 10 5-10 5L2 8zM2 12l10 5 10-5M2 16l10 5 10-5"/>',
    file: '<path d="M6 2h8l4 4v16H6zM14 2v5h5M9 12h6M9 16h6"/>',
    lock: '<rect x="4" y="10" width="16" height="11" rx="2"/><path d="M8 10V6a4 4 0 0 1 8 0v4M12 14v3"/>',
    unlock: '<rect x="4" y="10" width="16" height="11" rx="2"/><path d="M8 10V6a4 4 0 0 1 7.5-2M12 14v3"/>',
    eye: '<path d="M2 12s3.5-6 10-6 10 6 10 6-3.5 6-10 6S2 12 2 12Z"/><circle cx="12" cy="12" r="2.5"/>',
    filter: '<path d="M3 5h18l-7 8v6l-4 2v-8z"/>',
    power: '<path d="M12 2v10M5.5 5.5a9 9 0 1 0 13 0"/>',
    pause: '<path d="M8 5v14M16 5v14"/>',
    drive: '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M7 15h.01M11 15h6"/>',
    network: '<rect x="9" y="3" width="6" height="5" rx="1"/><rect x="3" y="16" width="6" height="5" rx="1"/><rect x="15" y="16" width="6" height="5" rx="1"/><path d="M12 8v4M6 16v-2a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v2"/>',
    book: '<path d="M4 4h6a3 3 0 0 1 3 3v14a3 3 0 0 0-3-3H4zM20 4h-4a3 3 0 0 0-3 3v14a3 3 0 0 1 3-3h4z"/>',
    help: '<circle cx="12" cy="12" r="9"/><path d="M9.5 9a2.6 2.6 0 1 1 4 2.2c-1 .6-1.5 1-1.5 2.3M12 17h.01"/>',
    list: '<path d="M9 6h12M9 12h12M9 18h12M4 6h.01M4 12h.01M4 18h.01"/>',
    grid: '<rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/>',
    phone: '<rect x="6" y="2" width="12" height="20" rx="2"/><path d="M10 18h4"/>',
    edit: '<path d="m4 20 4.5-1 11-11a2.1 2.1 0 0 0-3-3l-11 11zM14.5 7l3 3"/>',
  };

  function icon(name) {
    return `<svg class="icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${paths[name] || paths.info}</svg>`;
  }

  function hydrateIcons(root = document) {
    root.querySelectorAll('[data-icon]').forEach((element) => {
      element.classList.add('icon');
      element.innerHTML = icon(element.dataset.icon);
      element.removeAttribute('data-icon');
    });
  }

  function toast(message, tone = 'success') {
    let region = document.getElementById('toast-region');
    if (!region) {
      region = document.createElement('div');
      region.id = 'toast-region';
      region.setAttribute('aria-live', 'polite');
      document.body.append(region);
    }
    const item = document.createElement('div');
    item.className = 'mockup-toast';
    item.innerHTML = `<span class="icon">${icon(tone === 'success' ? 'check' : 'info')}</span><span></span>`;
    item.lastElementChild.textContent = message;
    region.append(item);
    setTimeout(() => item.remove(), 3600);
  }

  function dialog({ title, eyebrow = 'Construct', body = '', actions = [], wide = false }) {
    const element = document.createElement('dialog');
    element.className = 'mockup-dialog';
    if (wide) element.style.width = '760px';
    element.innerHTML = `
      <div class="dialog-head">
        <div><p class="dialog-eyebrow"></p><h2 class="dialog-title"></h2></div>
        <button class="dialog-close" aria-label="Close dialog"><span class="icon">${icon('x')}</span></button>
      </div>
      <div class="dialog-body"></div>
      <div class="dialog-actions"></div>`;
    element.querySelector('.dialog-eyebrow').textContent = eyebrow;
    element.querySelector('.dialog-title').textContent = title;
    element.querySelector('.dialog-body').innerHTML = body;
    const actionBar = element.querySelector('.dialog-actions');
    actions.forEach((action) => {
      const button = document.createElement('button');
      button.textContent = action.label;
      button.className = action.className || (action.primary ? 'primary-action' : 'secondary-action');
      button.addEventListener('click', () => {
        if (action.onClick) action.onClick(element);
        if (action.close !== false) element.close();
      });
      actionBar.append(button);
    });
    element.querySelector('.dialog-close').addEventListener('click', () => element.close());
    element.addEventListener('close', () => element.remove());
    document.body.append(element);
    element.showModal();
    return element;
  }

  function text(value) {
    const span = document.createElement('span');
    span.textContent = value;
    return span.innerHTML;
  }

  window.mockup = { icon, hydrateIcons, toast, dialog, text };
  document.addEventListener('DOMContentLoaded', () => hydrateIcons());
})();
