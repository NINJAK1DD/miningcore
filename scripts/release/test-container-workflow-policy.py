#!/usr/bin/env python3
"""Guard source-container cache, deadlines, provenance and Docker stage boundaries."""
from pathlib import Path
import re
import unittest
import yaml

ROOT = Path(__file__).resolve().parents[2]


class WorkflowPolicyTests(unittest.TestCase):
    def test_required_source_check_runs_for_every_dev_pull_request(self):
        workflow = yaml.load((ROOT / '.github/workflows/release.yml').read_text(), Loader=yaml.BaseLoader)
        trigger = workflow['on']['pull_request']
        self.assertEqual(['dev'], trigger['branches'])
        for key in ('paths', 'paths-ignore', 'branches-ignore', 'types'):
            self.assertNotIn(key, trigger, 'Required source check must report on every dev PR')
        job = workflow['jobs']['source-container-check']
        self.assertEqual('Test source container', job['name'])
        self.assertNotIn('if', job, 'Required source check must not be skipped by a job condition')
        self.assertNotIn('needs', job, 'Required source check must run independently of other jobs')
        self.assertNotIn(job.get('continue-on-error'), ('true', True))

    def test_required_source_check_name_is_unique(self):
        matches = []
        for path in sorted((ROOT / '.github/workflows').iterdir()):
            if path.suffix not in ('.yml', '.yaml'):
                continue
            workflow = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
            for job_id, job in workflow.get('jobs', {}).items():
                if job.get('name') == 'Test source container':
                    matches.append((path.name, job_id))
        self.assertEqual([('release.yml', 'source-container-check')], matches,
                         'A duplicate check name could satisfy the required gate with another job')

    def test_source_container_policy(self):
        workflow = yaml.load((ROOT / '.github/workflows/release.yml').read_text(), Loader=yaml.BaseLoader)
        job = workflow['jobs']['source-container-check']
        steps = job['steps']
        builds = [step for step in steps if 'docker/build-push-action@' in step.get('uses', '')]
        self.assertEqual(3, len(builds))
        self.assertTrue(all(int(step['timeout-minutes']) > 0 for step in builds))
        listener = next(step for step in steps if step['name'] == 'Test protected container listeners from host loopback')
        self.assertLessEqual(sum(int(step['timeout-minutes']) for step in builds)
                             + int(listener['timeout-minutes']), int(job['timeout-minutes']) - 5)
        for step in steps:
            self.assertNotIn(step.get('continue-on-error'), ('true', True))
            for key, value in step.get('with', {}).items():
                if key.startswith('cache-'): self.assertNotIn('ignore-error', value)
        self.assertEqual('dependency-cache', builds[0]['with']['target'])
        self.assertEqual('type=cacheonly', builds[0]['with']['outputs'])
        for step in builds[1:]:
            self.assertNotIn('cache-to', step['with'])
            self.assertEqual(builds[0]['with']['cache-from'], step['with']['cache-from'])
        budgets = [int(re.search(r'MININGCORE_CONTAINER_APT_TIMEOUT_SECONDS=(\d+)',
                                step['with']['build-args'])[1]) for step in builds]
        self.assertTrue(all(0 < budget <= 420 for budget in budgets))
        self.assertEqual(1, len(set(budgets)))
        # Reserve time for TERM/KILL, base pulls and the observed cold cache export.
        self.assertGreaterEqual(int(builds[0]['timeout-minutes']) * 60, budgets[0] + 10 + 180 + 120)
        tests = next(step for step in steps if step['name'] == 'Test bounded container build failure propagation')
        self.assertIn('python3 -B', tests['run'])
        checkout = next(step for step in steps if step['name'] == 'Checkout source')
        self.assertEqual('${{ env.SOURCE_SHA }}', checkout['with']['ref'])

    def test_source_checkout_and_every_image_reference_use_the_same_identity(self):
        workflow = yaml.load((ROOT / '.github/workflows/release.yml').read_text(), Loader=yaml.BaseLoader)
        job = workflow['jobs']['source-container-check']
        self.assertEqual('${{ github.event.pull_request.head.sha || github.sha }}', job['env']['SOURCE_SHA'])
        references = 0
        for step in job['steps']:
            for text, expected in ((step.get('run', ''), '"miningcore-source-test:$SOURCE_SHA"'),
                                   (step.get('with', {}).get('tags', ''), 'miningcore-source-test:${{ env.SOURCE_SHA }}')):
                count = text.count('miningcore-source-test:')
                self.assertEqual(count, text.count(expected), step['name'])
                references += count
        self.assertGreater(references, 1)

    def test_release_and_warmer_dependency_cache_inputs_match(self):
        release = yaml.load((ROOT / '.github/workflows/release.yml').read_text(), Loader=yaml.BaseLoader)
        warmer = yaml.load((ROOT / '.github/workflows/source-container-cache.yml').read_text(), Loader=yaml.BaseLoader)
        dependency = next(step['with'] for step in release['jobs']['source-container-check']['steps']
                          if step.get('with', {}).get('target') == 'dependency-cache')
        refresh = next(step['with'] for step in warmer['jobs']['dependencies']['steps']
                       if step.get('with', {}).get('target') == 'dependency-cache')
        # Even a lower, otherwise valid timeout changes the APT layer cache key.
        self.assertEqual(dependency['build-args'], refresh['build-args'])
        self.assertEqual(dependency['cache-to'], refresh['cache-to'])

    def test_default_branch_dependency_refresh_is_bounded_and_never_publishes(self):
        workflow = yaml.load((ROOT / '.github/workflows/source-container-cache.yml').read_text(), Loader=yaml.BaseLoader)
        self.assertEqual({'contents': 'read'}, workflow['permissions'])
        self.assertEqual({'push', 'schedule', 'workflow_dispatch'}, set(workflow['on']))
        self.assertEqual(['dev'], workflow['on']['push']['branches'])
        job = workflow['jobs']['dependencies']
        builds = [step for step in job['steps'] if 'docker/build-push-action@' in step.get('uses', '')]
        self.assertEqual(1, len(builds))
        build = builds[0]
        self.assertLess(int(build['timeout-minutes']), int(job['timeout-minutes']))
        options = build['with']
        self.assertEqual('dependency-cache', options['target'])
        self.assertEqual('type=cacheonly', options['outputs'])
        self.assertEqual('false', options['push'])
        self.assertEqual('true', options['no-cache'])
        self.assertEqual('true', options['pull'])
        self.assertNotIn('ignore-error', options['cache-to'])
        self.assertEqual('MININGCORE_CONTAINER_APT_TIMEOUT_SECONDS=420', options['build-args'])

    def test_standalone_source_image_ci_keeps_bounded_acquisition(self):
        workflow = yaml.load((ROOT / '.github/workflows/docker-image.yml').read_text(), Loader=yaml.BaseLoader)
        job = workflow['jobs']['build']
        build = next(step for step in job['steps'] if 'docker/build-push-action@' in step.get('uses', ''))
        budget = int(re.search(r'MININGCORE_CONTAINER_APT_TIMEOUT_SECONDS=(\d+)', build['with']['build-args'])[1])
        self.assertTrue(0 < budget <= 420)
        self.assertLess(int(build['timeout-minutes']), int(job['timeout-minutes']))

    def test_docker_dependency_and_default_image_boundaries(self):
        docker = (ROOT / 'Dockerfile').read_text()
        stages = re.findall(r'^FROM .* AS (\S+)$', docker, re.M)
        self.assertEqual('runtime', stages[-1])
        self.assertEqual(2, docker.count('source=scripts/release/container-apt-install.sh,'))
        self.assertEqual(2, docker.count('ARG MININGCORE_CONTAINER_APT_TIMEOUT_SECONDS=1800'))
        self.assertNotIn('COPY scripts/release/container-apt-install.sh', docker)
        self.assertIn('bash /tmp/container-apt-install.sh --install-recommends', docker)
        dependency = docker.split('FROM scratch AS dependency-cache\n')[1].split('\nFROM ')[0]
        self.assertIn('--from=build-dependencies', dependency)
        self.assertIn('--from=runtime-dependencies', dependency)
        self.assertNotIn('--from=builder ', dependency)
        self.assertIn('**/__pycache__/', (ROOT / '.dockerignore').read_text())


if __name__ == '__main__':
    unittest.main()
