#!/usr/bin/env python3
import contextlib
import importlib.util
import io
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location('fp_diagnostics', Path(__file__).with_name('report-floating-point-diagnostics.py'))
reporter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reporter)


class DiagnosticReporterTests(unittest.TestCase):
    def report(self, directory, namespace='', outcome='Passed', test_name='fixture', lines=None):
        tag = lambda name: f'{{{namespace}}}{name}' if namespace else name
        root = ET.Element(tag('TestRun'))
        results = ET.SubElement(root, tag('Results'))
        result = ET.SubElement(results, tag('UnitTestResult'), testName=test_name, outcome=outcome)
        output = ET.SubElement(result, tag('Output'))
        ET.SubElement(output, tag('StdOut')).text = '\n'.join(lines or [])
        ET.SubElement(output, tag('StdErr')).text = reporter.PREFIX + ' not captured fixture output'
        definitions = ET.SubElement(root, tag('TestDefinitions'))
        for index, (owner, method) in enumerate(sorted(reporter.REQUIRED_FIXTURES)):
            test_id = f'numeric-{index}'
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
                    self.assertEqual([('fixture', message)], list(reporter.diagnostics(path)))

    def test_ignores_synthetic_and_unrelated_messages(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.report(directory, lines=['Synthetic control regression: enabled=True',
                'Expected: ' + reporter.PREFIX, 'ordinary output'])
            self.assertEqual([], list(reporter.diagnostics(path)))

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
                    self.assertIn('missing executed numeric fixtures:', error.getvalue())
                    self.assertEqual('', output.getvalue())

    def test_fixture_identity_is_independent_of_display_name(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.report(directory, reporter.TRX_NAMESPACE)
            root = ET.parse(path).getroot()
            for result in root.findall(f'./{{{reporter.TRX_NAMESPACE}}}Results/*'):
                result.set('testName', 'display name with arguments: value, other')
            ET.ElementTree(root).write(path, encoding='utf-8')
            self.assertEqual([], list(reporter.diagnostics(path)))


if __name__ == '__main__':
    unittest.main()
