"""Focused fixtures for the root-level archive INSTALL.md."""

from pathlib import Path
import runpy
from tempfile import TemporaryDirectory
import unittest


install = runpy.run_path(str(Path(__file__).with_name('render-release-doc.py')))['install']
SOURCE = 'https://github.com/fixture/fork/blob/' + 'b' * 40


class GuideTests(unittest.TestCase):
    def setUp(self):
        tmp = TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name) / 'package'
        self.docs = self.root / 'docs'
        self.docs.mkdir(parents=True)
        (self.docs / 'db (setup).md').touch()

    def render(self, text):
        (self.docs / 'releases.md').write_text(text, encoding='utf-8')
        install(self.root, SOURCE)
        return (self.root / 'INSTALL.md').read_text(encoding='utf-8')

    def test_destinations(self):
        for original, expected in (
                ('[db](<db (setup).md#roles> "title")', '[db](<docs/db%20%28setup%29.md#roles> "title")'),
                ('[db]: <db (setup).md>', '[db]: <docs/db%20%28setup%29.md>'),
                ('[lab]: ../scripts/lab.md?q=1#start', f'[lab]: {SOURCE}/scripts/lab.md?q=1#start'),
                ('[lab](../scripts/lab(v2).md)', f'[lab]({SOURCE}/scripts/lab%28v2%29.md)'),
                ('[local](#start) [src](https://example.com/x)', '[local](#start) [src](https://example.com/x)')):
            with self.subTest(original=original):
                self.assertEqual(expected, self.render(original))

    def test_literal_examples_unchanged(self):
        literal = '[db](db.md)'
        for example in (f'`{literal}`', f'``one `\n{literal}``',
                        f'    {literal}', f'\t{literal}', f'~~~sh\n{literal}\n~~~',
                        f'````md\n```\n{literal}\n````'):
            with self.subTest(example=example):
                self.assertEqual(example + f'\n[real]({SOURCE}/docs/db.md)',
                                 self.render(example + '\n[real](db.md)'))

    def test_escape_refused_even_if_file_exists_outside(self):
        (self.root.parent / 'outside').touch()
        for path in ('../../outside', '%2e%2e/%2e%2e/new', '/outside'):
            with self.subTest(path=path), self.assertRaisesRegex(ValueError, 'leaves package'):
                self.render(f'[outside]({path})')


if __name__ == '__main__':
    unittest.main()
