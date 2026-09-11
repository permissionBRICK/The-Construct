"""Local Chromium regression checks; requires Playwright and its Chromium browser.

Set CONSTRUCT_TEST_CHROMIUM to use an existing Chromium executable.
No Windows host, VM, ISO download, or running gateway is required.
"""
import os
from pathlib import Path
import unittest

try:
    from playwright.sync_api import sync_playwright
except ImportError:
    sync_playwright = None

STATIC = Path(__file__).with_name('static')


@unittest.skipIf(sync_playwright is None, 'Install Playwright to run local browser checks')
class RenderingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.playwright = sync_playwright().start()
        cls.browser = cls.playwright.chromium.launch(
            executable_path=os.environ.get('CONSTRUCT_TEST_CHROMIUM'),
            args=['--no-sandbox'])

    @classmethod
    def tearDownClass(cls):
        cls.browser.close()
        cls.playwright.stop()

    def setUp(self):
        self.page = self.browser.new_page()
        self.errors = []
        self.page.on('pageerror', lambda error: self.errors.append(str(error)))
        self.page.route('http://localhost:6080/**', lambda route: route.fulfill(
            content_type='text/html', body='<!doctype html><body></body>'))
        self.socket_handler = None
        self.page.route_web_socket('**', lambda socket: self.socket_handler(socket))
        self.page.goto('http://localhost:6080/')
        self.page.add_script_tag(path=os.environ.get(
            'CONSTRUCT_TEST_GUACAMOLE', str(STATIC / 'guacamole-1.6.0.min.js')))
        self.page.evaluate('''() => {
            window.counts = {decoded: 0, closed: 0, decoderClosed: 0};
            const decode = ImageDecoder.prototype.decode;
            const closeDecoder = ImageDecoder.prototype.close;
            const closeFrame = VideoFrame.prototype.close;
            const frames = new WeakSet();
            ImageDecoder.prototype.decode = function(...args) {
                return decode.apply(this, args).then(result => {
                    counts.decoded++; frames.add(result.image); return result;
                });
            };
            ImageDecoder.prototype.close = function() {
                counts.decoderClosed++; return closeDecoder.call(this);
            };
            VideoFrame.prototype.close = function() {
                if (frames.has(this)) { counts.closed++; frames.delete(this); }
                return closeFrame.call(this);
            };
            window.check = (value, message) => { if (!value) throw Error(message); };
            window.tick = () => new Promise(resolve => setTimeout(resolve, 0));
            window.bounded = promise => Promise.race([promise, new Promise((_, reject) =>
                setTimeout(() => reject(Error('Display queue stalled')), 3000))]);
            window.makeDisplay = () => {
                const display = new Guacamole.Display();
                display.resize(display.getDefaultLayer(), 8, 8);
                display.flush();
                return display;
            };
            window.png = (() => {
                const canvas = document.createElement('canvas');
                canvas.width = canvas.height = 8;
                const context = canvas.getContext('2d');
                context.fillStyle = '#ff0000'; context.fillRect(0, 0, 8, 8);
                return canvas.toDataURL('image/png').split(',')[1];
            })();
            window.sendImage = (display, data = png, mime = 'image/png') => {
                const stream = new Guacamole.InputStream({sendAck() {}}, 1);
                display.drawStream(display.getDefaultLayer(), 0, 0, stream, mime);
                if (stream.onblob) stream.onblob(data);
                if (stream.onend) stream.onend();
                return stream;
            };
            window.flush = display => bounded(new Promise(resolve => display.flush(resolve)));
        }''')

    def tearDown(self):
        self.page.close()
        self.assertEqual(self.errors, [])

    def test_repeated_native_decodes_release_frames_and_keep_rendering(self):
        self.page.evaluate('''async () => {
            const display = makeDisplay();
            for (let i = 0; i < 300; i++) {
                sendImage(display);
                await flush(display);
            }
            const pixel = display.getDefaultLayer().getCanvas().getContext('2d')
                .getImageData(0, 0, 1, 1).data;
            check(pixel[0] === 255 && pixel[1] === 0 && pixel[3] === 255, 'Image not drawn');
            check(counts.decoded === 300, 'Native decoder not exercised');
            check(counts.closed === 300, 'Decoded frames leaked');
            check(counts.decoderClosed === 300, 'Image decoders leaked');
        }''')

    def test_failed_decode_unblocks_queue_and_reports_error(self):
        self.page.evaluate('''async () => {
            const display = makeDisplay();
            const errors = []; display.onerror = error => errors.push(error.message);
            sendImage(display, btoa('not a PNG'));
            await flush(display);
            check(errors.length === 1, 'Decode error not surfaced');
            sendImage(display);
            await flush(display);
            check(counts.closed === 1, 'Valid image after bad image not released');
        }''')

    def test_cancel_while_decoding_and_after_decode_releases_resources(self):
        self.page.evaluate('''async () => {
            const display = makeDisplay();
            const errors = []; display.onerror = error => errors.push(error);
            sendImage(display);
            display.cancel(); // Native decoder promise has not resolved.
            await tick();
            check(counts.closed === counts.decoded, 'Cancellation leaked late frame');
            check(counts.decoderClosed === 1, 'Cancellation leaked decoder');
            const before = counts.decoded;
            sendImage(display); // Decoded frame waiting for a future flush.
            await bounded((async () => { while (counts.decoded === before) await tick(); })());
            display.cancel();
            await tick();
            check(counts.closed === counts.decoded, 'Queued frame leaked on cancel');
            check(counts.decoderClosed === 2, 'Second decoder leaked');
            check(errors.length === 0, 'Intentional cancel surfaced an error');
            sendImage(display); await flush(display);
            check(counts.closed === counts.decoded, 'Rendering after cancel leaked');
        }''')

    def test_drawing_failure_releases_frame_and_reports_error(self):
        self.page.evaluate('''async () => {
            const display = makeDisplay();
            const errors = []; display.onerror = error => errors.push(error);
            display.getDefaultLayer().drawImage = () => { throw Error('draw failed'); };
            sendImage(display); await flush(display);
            check(errors.length === 1, 'Draw failure not surfaced');
            check(counts.closed === 1 && counts.decoderClosed === 1, 'Draw failure leaked resources');
        }''')

    def test_image_decoded_before_protocol_end_does_not_feed_closed_stream(self):
        self.page.evaluate('''async () => {
            const display = makeDisplay();
            const stream = new Guacamole.InputStream({sendAck() {}}, 1);
            display.drawStream(display.getDefaultLayer(), 0, 0, stream, 'image/png');
            stream.onblob(png);
            await flush(display); // PNG is complete, but protocol stream is still open.
            if (stream.onblob) stream.onblob(btoa('trailing data'));
            if (stream.onend) stream.onend();
            await tick();
            check(counts.closed === 1 && counts.decoderClosed === 1, 'Early decode leaked resources');
        }''')

    def test_browser_without_image_decoder_retains_fallback(self):
        self.page.evaluate('''async () => {
            window.ImageDecoder = undefined;
            const display = makeDisplay();
            sendImage(display); await flush(display);
            const pixel = display.getDefaultLayer().getCanvas().getContext('2d')
                .getImageData(0, 0, 1, 1).data;
            check(pixel[0] === 255 && pixel[3] === 255, 'Fallback did not draw');
        }''')

    def test_decoder_constructor_failure_does_not_block_queue(self):
        self.page.evaluate('''async () => {
            window.ImageDecoder = class { constructor() { throw Error('Unsupported codec'); } };
            const display = makeDisplay();
            let errors = 0; display.onerror = () => errors++;
            sendImage(display); await flush(display);
            check(errors === 1, 'Constructor failure not surfaced');
        }''')

    def test_viewer_reconnects_and_retains_decode_error(self):
        # Exercise the actual viewer lifecycle over a controlled WebSocket.
        html = (STATIC / 'index.html').read_text().split('<body>', 1)[1].split('</body>', 1)[0]
        self.page.evaluate('(html) => document.body.innerHTML = html', html)
        self.page.evaluate("history.replaceState(null, '', '/#test.ticket')")
        self.page.route('http://localhost:6080/redeem', lambda route: route.fulfill(
            json={'id': 'test', 'name': 'test-vm'}))
        sockets = []

        def connected(socket):
            sockets.append(socket)
            socket.on_message(lambda text: socket.send(text) if ',4.ping,' in text else None)
            socket.send('0.,4.test;4.size,1.0,1.8,1.8;4.sync,1.1;')

        self.socket_handler = connected
        self.page.add_script_tag(path=str(STATIC / 'viewer.js'))

        def image(data, timestamp):
            def instruction(*args):
                return ','.join(f'{len(str(arg))}.{arg}' for arg in args) + ';'
            return (instruction('img', 1, 12, 0, 'image/png', 0, 0)
                    + instruction('blob', 1, data) + instruction('end', 1)
                    + instruction('sync', timestamp))

        for attempt in range(3):
            self.page.wait_for_function("document.querySelector('#status').textContent.startsWith('Connected')")
            sockets[-1].send(image(self.page.evaluate('png'), attempt + 2))
            self.page.wait_for_function('(expected) => counts.closed === expected', arg=attempt + 1)
            self.page.locator('#reconnect').click()
        self.page.wait_for_function("document.querySelector('#status').textContent.startsWith('Connected')")
        sockets[-1].send(image('bm90IGFuIGltYWdl', 9))
        self.page.wait_for_function("document.querySelector('#status').classList.contains('error')")
        self.assertIn('could not be decoded', self.page.locator('#status').inner_text())
        self.assertTrue(self.page.locator('#cad').is_disabled())
        self.page.locator('#reconnect').click()
        self.page.wait_for_function("document.querySelector('#status').textContent.startsWith('Connected')")
        sockets[-1].send(image(self.page.evaluate('png'), 10))
        self.page.wait_for_function('counts.closed === 4')
        self.assertFalse(self.page.locator('#cad').is_disabled())
        self.page.locator('#disconnect').click()


if __name__ == '__main__':
    unittest.main()
