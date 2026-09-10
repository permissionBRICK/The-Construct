'use strict';
const screen = document.querySelector('#screen');
const status = document.querySelector('#status');
const cad = document.querySelector('#cad');
let client, keyboard, id;
function message(text, error = false) { status.textContent = text; status.classList.toggle('error', error); }
function connect() {
    if (!id) return;
    if (client) client.disconnect();
    if (keyboard) keyboard.reset();
    screen.replaceChildren();
    const tunnel = new Guacamole.WebSocketTunnel(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/ws/${id}`);
    client = new Guacamole.Client(tunnel);
    const active = client;
    const display = active.getDisplay();
    screen.append(display.getElement());
    function fit() {
        if (!display.getWidth()) return;
        const scale = Math.min(1, (document.fullscreenElement ? innerWidth : innerWidth - 48) / display.getWidth(), (innerHeight - 160) / display.getHeight());
        display.scale(Math.max(0.1, scale));
        screen.style.width = `${display.getWidth() * display.getScale()}px`;
        screen.style.height = `${display.getHeight() * display.getScale()}px`;
    }
    display.onresize = fit;
    window.onresize = fit;
    active.onerror = error => { message(error.message || 'Connection failed. Reconnect or create a new console link.', true); cad.disabled = true; };
    active.onstatechange = state => {
        if (active !== client) return;
        if (state === 3) { message('Connected · click the display to control the guest'); cad.disabled = false; }
        else if (state === 5) { message('Disconnected. Reconnect while this link is valid, or create a new link.'); cad.disabled = true; }
        else if (state < 3) message('Connecting to guest…');
    };
    // Guacamole sends X11 keysyms through RDP, avoiding the WMI TypeText path.
    keyboard = new Guacamole.Keyboard(screen);
    keyboard.onkeydown = keysym => { active.sendKeyEvent(1, keysym); return false; };
    keyboard.onkeyup = keysym => active.sendKeyEvent(0, keysym);
    screen.onblur = () => keyboard.reset();
    screen.oncontextmenu = event => event.preventDefault();
    const mouse = new Guacamole.Mouse(display.getElement());
    mouse.onmousedown = state => { screen.focus({preventScroll: true}); active.sendMouseState(state, true); };
    mouse.onmouseup = mouse.onmousemove = state => active.sendMouseState(state, true);
    const touch = new Guacamole.Mouse.Touchscreen(display.getElement());
    touch.onmousedown = touch.onmouseup = touch.onmousemove = state => active.sendMouseState(state, true);
    active.connect('');
}
document.querySelector('#disconnect').onclick = () => { keyboard?.reset(); client?.disconnect(); };
document.querySelector('#reconnect').onclick = connect;
document.querySelector('#fullscreen').onclick = () => screen.requestFullscreen().catch(() => message('Full screen is unavailable.'));
cad.onclick = () => {
    if (!client) return;
    for (const key of [0xffe3, 0xffe9, 0xffff]) client.sendKeyEvent(1, key);
    for (const key of [0xffff, 0xffe9, 0xffe3]) client.sendKeyEvent(0, key);
};
window.addEventListener('beforeunload', () => client?.disconnect());
(async () => {
    const ticket = location.hash.slice(1);
    history.replaceState(null, '', location.pathname);
    if (!ticket) return message('Open the complete console link printed by Construct.', true);
    try {
        const response = await fetch('/redeem', {method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({ticket})});
        if (!response.ok) throw new Error(await response.text());
        const session = await response.json();
        id = session.id;
        document.querySelector('#name').textContent = session.name;
        document.title = `${session.name} · Construct console`;
        connect();
    } catch (error) { message(error.message, true); }
})();
