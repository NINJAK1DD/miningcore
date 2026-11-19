"""Focused fixtures for the root-level archive INSTALL.md."""

from pathlib import Path
import runpy
from tempfile import TemporaryDirectory
import unittest


install = runpy.run_path(str(Path(__file__).with_name('render-release-doc.py')))['install']
check = runpy.run_path(str(Path(__file__).with_name('test-packaged-guide.py')))['check']
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

    def test_lists_wrapped_labels_and_unmatched_ticks(self):
        (self.docs / 'db.md').touch()
        for before in ('1. Step one:\n\n    Continue with ', '- outer\n    - nested ',
                       '- Before upgrading:\n    ', 'An unmatched ` before ',
                       '- First `\n- Second ', 'A paragraph\n    continued with '):
            with self.subTest(before=before):
                self.assertEqual(before + '[read the\nguide](docs/db.md)',
                                 self.render(before + '[read the\nguide](db.md)'))
        code = '- outer\n\n      [literal](db.md)\n\n  ~~~\n  [literal](db.md)\n  ~~~\n'
        self.assertEqual(code, self.render(code))
        escaped = r'\` [db](db.md) \`'
        self.assertEqual(escaped.replace('](db.md)', '](docs/db.md)'), self.render(escaped))

    def test_archive_check_is_independent_of_rewriter(self):
        route = 'upgrade-v9.0.0-rc.3.md'
        (self.docs / route).touch()
        for parent in (self.root, self.docs):
            (parent / 'db.md').touch()  # A stale path can resolve to the wrong shipped file.
        guide = self.root / 'INSTALL.md'
        good = self.render(f'[upgrade]({route})\n\n- outer\n    - [read the\nguide](db.md)\n'
                           '\n`[literal](db.md)`\n\n```sh\nNEXT_VERSION=v9.0.0-rc.3\n```\n')
        check(self.root, SOURCE)
        for bad in (good.replace('](docs/db.md)', '](db.md)'),
                    good.replace('[literal](db.md)', '[literal](docs/db.md)'),
                    good.replace('](docs/db.md)', '](docs/missing.md)')):
            with self.subTest(bad=bad):
                guide.write_text(bad, encoding='utf-8')
                with self.assertRaises(ValueError):
                    check(self.root, SOURCE)


if __name__ == '__main__':
    unittest.main()
