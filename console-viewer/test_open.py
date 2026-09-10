import importlib.util
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('viewer_open', Path(__file__).with_name('open.py'))
opener = importlib.util.module_from_spec(spec)
spec.loader.exec_module(opener)


class ForwardReuseTests(unittest.TestCase):
    def test_second_link_reuses_existing_console_forward_without_creating_another(self):
        output = 'ID PORT TARGET STATUS LABEL URL\na 6080 client open Guest console http://localhost:18816/\n'
        with patch.object(opener.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, output, '')) as run:
            self.assertEqual(opener.expose_viewer('6080'), 'http://localhost:18816/')
            self.assertEqual(run.call_count, 1)
            self.assertEqual(run.call_args.args[0], ['construct', 'expose', '--list'])

    def test_wrong_port_and_queued_forward_do_not_get_reused(self):
        output = 'a 6081 client open Guest console http://localhost:18816/\nb 6080 client queued Guest console -\n'
        with patch.object(opener.subprocess, 'run', side_effect=[subprocess.CompletedProcess([], 0, output, ''), subprocess.CompletedProcess([], 0, 'http://localhost:6080/\n', '')]) as run:
            self.assertEqual(opener.expose_viewer(6080), 'http://localhost:6080/')
            self.assertEqual(run.call_count, 2)

    def test_failed_forward_creation_is_reported(self):
        with patch.object(opener.subprocess, 'run', side_effect=[subprocess.CompletedProcess([], 0, '', ''), subprocess.CompletedProcess([], 6, '', 'No Construct client attached')]):
            with self.assertRaisesRegex(RuntimeError, 'No Construct client attached'):
                opener.expose_viewer(6080)
