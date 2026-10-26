#!/usr/bin/env python3
"""Guard source-container cache, deadlines, provenance and Docker stage boundaries."""
from pathlib import Path
import re
import unittest
import yaml

ROOT = Path(__file__).resolve().parents[2]


class WorkflowPolicyTests(unittest.TestCase):
    def test_source_container_policy(self):
        workflow = yaml.load((ROOT / '.github/workflows/release.yml').read_text(), Loader=yaml.BaseLoader)
        job = workflow['jobs']['source-container-check']
        steps = job['steps']
        builds = [step for step in steps if 'docker/build-push-action@' in step.get('uses', '')]
        self.assertEqual(3, len(builds))
        self.assertEqual(['13', '20', '5'], [step['timeout-minutes'] for step in builds])
        self.assertLess(sum(int(step['timeout-minutes']) for step in builds) + 2, int(job['timeout-minutes']))
        for step in steps:
            self.assertNotIn(step.get('continue-on-error'), ('true', True))
            for key, value in step.get('with', {}).items():
                if key.startswith('cache-'): self.assertNotIn('ignore-error', value)
        self.assertEqual('dependency-cache', builds[0]['with']['target'])
        self.assertEqual('type=cacheonly', builds[0]['with']['outputs'])
        for step in builds[1:]:
            self.assertNotIn('cache-to', step['with'])
        self.assertTrue(all('MININGCORE_CONTAINER_APT_TIMEOUT_SECONDS=600' in step['with']['build-args'] for step in builds))
        tests = next(step for step in steps if step['name'] == 'Test bounded container build failure propagation')
        self.assertIn('python3 -B', tests['run'])
        checkout = next(step for step in steps if step['name'] == 'Checkout source')
        self.assertEqual('${{ github.event.pull_request.head.sha || github.sha }}', checkout['with']['ref'])

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
