#!/usr/bin/env python3
import contextlib
import importlib.util
import io
from pathlib import Path
import re
import tempfile
import unittest
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location('fp_diagnostics', Path(__file__).with_name('report-floating-point-diagnostics.py'))
reporter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reporter)


class DiagnosticReporterTests(unittest.TestCase):
    def test_vardiff_source_vectors_match_reporter_contract(self):
        identity = next(key for key in reporter.REQUIRED_FIXTURES
                        if key[0] == 'Miningcore.Tests.VarDiffManagerTests')
        source_path = Path(__file__).resolve().parents[2] / 'src/Miningcore.Tests/VarDiffManagerTests.cs'
        source = source_path.read_text(encoding='utf-8')
        # This fixture uses literal InlineData; fail clearly if its declaration
        # changes instead of accidentally counting attributes on another method.
        pattern = (r'((?:^[ \t]*\[[^\]\r\n]+\][ \t]*\r?\n)+)^[ \t]*public[ \t]+void[ \t]+'
                   + re.escape(identity[1]) + r'[ \t]*\(')
        matches = list(re.finditer(pattern, source, re.MULTILINE))
        self.assertEqual(1, len(matches), f'Cannot uniquely locate InlineData for {source_path}:{identity[1]}')
        arguments = re.findall(r'\[\s*InlineData\s*\((.*?)\)\s*\]', matches[0].group(1), re.DOTALL)
        vectors = [tuple(re.sub(r'\s+', '', argument_list).split(',')) for argument_list in arguments]
        expected_count = reporter.REQUIRED_FIXTURES[identity]
        self.assertEqual(expected_count, len(vectors),
                         f'{identity[1]} has {len(vectors)} InlineData rows, but REQUIRED_FIXTURES expects '
                         f'{expected_count}; update the reporter count deliberately when changing vectors')
        # Keep the independent boundary-vector manifest deliberate too: four
        # distinct test IDs must not conceal an accidentally duplicated vector.
        self.assertCountEqual([
            ('1e300', '1e10', '1e20', '1e290'),
            ('1e-300', '1e-10', '0.001', '1e-307'),
            ('1e-300', '1e-30', '1', 'double.Epsilon'),
            ('1e306', '10', '0.001', 'double.MaxValue'),
        ], vectors, 'VarDiff boundary vectors changed; verify and update the independent vector manifest deliberately')

    def report(self, directory, namespace='', outcome='Passed', test_name='fixture', lines=None):
        tag = lambda name: f'{{{namespace}}}{name}' if namespace else name
        root = ET.Element(tag('TestRun'))
        results = ET.SubElement(root, tag('Results'))
        result = ET.SubElement(results, tag('UnitTestResult'), testName=test_name, outcome=outcome)
        output = ET.SubElement(result, tag('Output'))
        ET.SubElement(output, tag('StdOut')).text = '\n'.join(lines or [])
        ET.SubElement(output, tag('StdErr')).text = reporter.PREFIX + ' not captured fixture output'
        definitions = ET.SubElement(root, tag('TestDefinitions'))
        for index, ((owner, method), count) in enumerate(sorted(reporter.REQUIRED_FIXTURES.items())):
            for case in range(count):
                test_id = f'numeric-{index}-{case}'
                ET.SubElement(results, tag('UnitTestResult'), testId=test_id, testName=method, outcome=outcome)
                definition = ET.SubElement(definitions, tag('UnitTest'), id=test_id)
                ET.SubElement(definition, tag('TestMethod'), className=owner, name=method)
        path = Path(directory) / 'results.trx'
        ET.ElementTree(root).write(path, encoding='utf-8', xml_declaration=True)
        return path

    def test_reads_passing_and_failed_namespace_reports(self):
        for namespace in ('', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'):
            for outcome in ('Passed', 'Failed'):
                with self.subTest(namespace=namespace, outcome=outcome), tempfile.TemporaryDirectory() as directory:
                    message = reporter.PREFIX + ' on managed thread 42. Saved: MXCSR=0x9fc0'
                    path = self.report(directory, namespace, outcome, lines=[message])
                    self.assertEqual(([('fixture', message)], []), reporter.read_report(path))

    def test_ignores_synthetic_and_unrelated_messages(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.report(directory, lines=['Synthetic control regression: enabled=True',
                'Expected: ' + reporter.PREFIX, 'ordinary output'])
            self.assertEqual(([], []), reporter.read_report(path))

    def test_escapes_command_data_and_test_names(self):
        warning = reporter.workflow_warning('fixture%\r\n::error::injected', reporter.PREFIX + ' 100%')
        self.assertTrue(warning.startswith('::warning title=Non-default floating-point controls::'))
        self.assertIn('fixture%25%0D%0A::error::injected', warning)
        self.assertIn('100%25', warning)
        self.assertEqual(1, len(warning.splitlines()))

    def test_cli_deduplicates_and_emits_from_runner_stdout(self):
        with tempfile.TemporaryDirectory() as directory:
            message = reporter.PREFIX + ' saved/default controls'
            path = self.report(directory, lines=[message, message])
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                status = reporter.main([str(path), str(path)])
            self.assertEqual(0, status)
            self.assertEqual(1, sum(line.startswith('::warning ') for line in output.getvalue().splitlines()))
            self.assertIn('1 distinct test warning(s)', output.getvalue())

    def test_missing_or_malformed_evidence_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'missing.trx'
            for malformed in (False, True):
                if malformed:
                    path.write_text('<TestRun>', encoding='utf-8')
                with self.subTest(malformed=malformed), contextlib.redirect_stderr(io.StringIO()):
                    self.assertEqual(1, reporter.main([str(path)]))

    def test_well_formed_xml_requires_meaningful_trx_results(self):
        documents = (
            '<unrelated><Results><UnitTestResult /></Results></unrelated>',
            '<TestRun />',
            '<TestRun><Results /></TestRun>',
            '<TestRun><Other><UnitTestResult /></Other></TestRun>',
            '<TestRun xmlns="urn:unrelated"><Results><UnitTestResult /></Results></TestRun>',
            '<TestRun><Results xmlns="urn:unrelated"><UnitTestResult /></Results></TestRun>',
            f'<TestRun xmlns="{reporter.TRX_NAMESPACE}"><Results /></TestRun>',
        )
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'invalid.trx'
            for document in documents:
                path.write_text(document, encoding='utf-8')
                error = io.StringIO()
                output = io.StringIO()
                with self.subTest(document=document), contextlib.redirect_stderr(error), contextlib.redirect_stdout(output):
                    self.assertEqual(1, reporter.main([str(path)]))
                    self.assertIn('Cannot read floating-point diagnostic report:', error.getvalue())
                    self.assertEqual('', output.getvalue())

    def test_required_fixtures_need_matching_executed_results(self):
        for change in ('no-definitions', 'no-results', 'wrong-id', 'wrong-class',
                       'wrong-method', 'skipped', 'missing-outcome'):
            for namespace in ('', reporter.TRX_NAMESPACE):
                with self.subTest(change=change, namespace=namespace), tempfile.TemporaryDirectory() as directory:
                    path = self.report(directory, namespace)
                    root = ET.parse(path).getroot()
                    prefix = f'{{{namespace}}}' if namespace else ''
                    definitions = root.find(prefix + 'TestDefinitions')
                    results = root.find(prefix + 'Results')
                    result = results[1]
                    method = definitions[0].find(prefix + 'TestMethod')
                    if change == 'no-definitions':
                        root.remove(definitions)
                    elif change == 'no-results':
                        results.remove(result)
                    elif change == 'wrong-id':
                        result.set('testId', 'unrelated')
                    elif change == 'wrong-class':
                        method.set('className', 'Unrelated.Fixture')
                    elif change == 'wrong-method':
                        method.set('name', method.get('name') + '_Unrelated')
                    elif change == 'skipped':
                        result.set('outcome', 'NotExecuted')
                    else:
                        result.attrib.pop('outcome')
                    ET.ElementTree(root).write(path, encoding='utf-8')
                    output = io.StringIO()
                    error = io.StringIO()
                    with contextlib.redirect_stdout(output), contextlib.redirect_stderr(error):
                        self.assertEqual(1, reporter.main([str(path)]))
                    self.assertIn('incomplete numeric fixture evidence:', error.getvalue())
                    self.assertEqual('', output.getvalue())

    def test_fixture_identity_is_independent_of_display_name(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.report(directory, reporter.TRX_NAMESPACE)
            root = ET.parse(path).getroot()
            for result in root.findall(f'./{{{reporter.TRX_NAMESPACE}}}Results/*'):
                result.set('testName', 'display name with arguments: value, other')
            ET.ElementTree(root).write(path, encoding='utf-8')
            self.assertEqual(([], []), reporter.read_report(path))

    def test_partial_runs_emit_warnings_before_failing_coverage(self):
        for namespace in ('', reporter.TRX_NAMESPACE):
            for outcome in ('NotExecuted', 'Aborted', 'Timeout', 'Error'):
                with self.subTest(namespace=namespace, outcome=outcome), tempfile.TemporaryDirectory() as directory:
                    message = reporter.PREFIX + ' saved MXCSR=0x9fc0'
                    path = self.report(directory, namespace, lines=[message, message])
                    root = ET.parse(path).getroot()
                    prefix = f'{{{namespace}}}' if namespace else ''
                    root.find(prefix + 'Results')[1].set('outcome', outcome)
                    ET.ElementTree(root).write(path, encoding='utf-8')
                    combined = io.StringIO()
                    with contextlib.redirect_stdout(combined), contextlib.redirect_stderr(combined):
                        self.assertEqual(1, reporter.main([str(path)]))
                    lines = combined.getvalue().splitlines()
                    self.assertTrue(lines[0].startswith('::warning '))
                    self.assertIn(message, lines[0])
                    self.assertEqual(1, sum(line.startswith('::warning ') for line in lines))
                    self.assertIn('incomplete numeric fixture evidence:', lines[1])
                    self.assertNotIn('distinct test warning(s)', combined.getvalue())

    def test_all_reports_are_scanned_even_when_earlier_evidence_is_invalid(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            first = root / 'partial'
            second = root / 'complete'
            first.mkdir()
            second.mkdir()
            partial = self.report(first, lines=[reporter.PREFIX + ' first'])
            tree = ET.parse(partial)
            tree.getroot().remove(tree.getroot().find('TestDefinitions'))
            tree.write(partial, encoding='utf-8')
            complete = self.report(second, lines=[reporter.PREFIX + ' second'])
            malformed = root / 'malformed.trx'
            malformed.write_text('<TestRun>', encoding='utf-8')
            for early in (partial, root / 'missing.trx', malformed):
                combined = io.StringIO()
                with self.subTest(early=early), contextlib.redirect_stdout(combined), contextlib.redirect_stderr(combined):
                    self.assertEqual(1, reporter.main([str(early), str(complete)]))
                lines = combined.getvalue().splitlines()
                self.assertTrue(any(line.startswith('::warning ') and ' second' in line for line in lines))
                self.assertTrue(lines[-1].startswith('Cannot read floating-point diagnostic report:'))
                if early == partial:
                    self.assertIn(' first', lines[0])

    def test_all_four_theory_cases_are_required_and_duplicate_ids_do_not_count(self):
        for change in ('removed', 'skipped', 'duplicate-id', 'extra-case'):
            with self.subTest(change=change), tempfile.TemporaryDirectory() as directory:
                path = self.report(directory)
                tree = ET.parse(path)
                results = tree.getroot().find('Results')
                vardiff = [result for result in results if result.get('testName').startswith('ExtremeRatios_')]
                self.assertEqual(4, len(vardiff))
                if change == 'removed':
                    results.remove(vardiff[0])
                elif change == 'skipped':
                    vardiff[0].set('outcome', 'NotExecuted')
                elif change == 'duplicate-id':
                    vardiff[0].set('testId', vardiff[1].get('testId'))
                else:
                    ET.SubElement(results, 'UnitTestResult', testId='extra', outcome='Passed')
                    definition = ET.SubElement(tree.getroot().find('TestDefinitions'), 'UnitTest', id='extra')
                    ET.SubElement(definition, 'TestMethod', className='Miningcore.Tests.VarDiffManagerTests',
                                  name='ExtremeRatios_AvoidIntermediateOverflowAndUnderflow')
                tree.write(path, encoding='utf-8')
                error = io.StringIO()
                with contextlib.redirect_stderr(error):
                    self.assertEqual(1, reporter.main([str(path)]))
                self.assertIn('expected 4 distinct executed case(s)', error.getvalue())


if __name__ == '__main__':
    unittest.main()
