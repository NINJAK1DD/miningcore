#!/usr/bin/env python3
"""Guard source-container cache, deadlines, provenance and Docker stage boundaries."""
from pathlib import Path
from fnmatch import fnmatchcase
import re
import unittest
import xml.etree.ElementTree as ET
import yaml

ROOT = Path(__file__).resolve().parents[2]
SDK_WORKFLOWS = ('dotnet.yml', 'release.yml', 'stratum-portability.yml')


def require_coordinated_sdk(versions, image_version):
    if set(versions) != set(SDK_WORKFLOWS) or any(version != image_version for version in versions.values()):
        paths = ', '.join('.github/workflows/' + name for name in SDK_WORKFLOWS)
        raise ValueError(f'SDK image {image_version} and CI_DOTNET_SDK must advance together. '
                         f'Update CI_DOTNET_SDK in all three files: {paths}. '
                         f'Dependabot updates the Dockerfile, not these workflow variables. Found: {versions}')


def serviced_image(docker, family, minimum, stage=None):
    if not re.fullmatch(r'10\.0\.[0-9]+', minimum):
        raise ValueError('Expected a stable .NET 10 policy minimum')
    suffix = ' AS ' + re.escape(stage) if stage else ''
    matches = re.findall(r'^FROM (mcr\.microsoft\.com/dotnet/' + family +
                         r':(10\.0\.[0-9]+)-resolute@sha256:[0-9a-f]{64})' + suffix + r'$',
                         docker, re.M)
    if len(matches) != 1 or int(matches[0][1].split('.')[-1]) < int(minimum.split('.')[-1]):
        raise ValueError('Expected one digest-pinned .NET 10 image at or above the reviewed minimum')
    return matches[0]


class WorkflowPolicyTests(unittest.TestCase):
    def test_container_servicing_tags_match_reviewed_policy(self):
        policy = ET.parse(ROOT / 'src/Miningcore/RuntimeDependencyPolicy.props')
        minimum = policy.findtext('./PropertyGroup/MiningcoreRuntimeMinimum')
        versions = {name: yaml.load((ROOT / '.github/workflows' / name).read_text(),
                                   Loader=yaml.BaseLoader).get('env', {}).get('CI_DOTNET_SDK')
                    for name in SDK_WORKFLOWS}
        source = (ROOT / 'Dockerfile').read_text()
        packaged = (ROOT / 'packaging/docker/Dockerfile.release').read_text()
        private_minimum = policy.findtext('./PropertyGroup/MiningcorePrivateSdkMinimum')
        require_coordinated_sdk(versions, serviced_image(source, 'sdk', private_minimum, 'build-dependencies')[1])
        runtime = serviced_image(source, 'aspnet', minimum, 'runtime-dependencies')
        self.assertEqual(runtime, serviced_image(packaged, 'aspnet', minimum))

    def test_sdk_drift_names_every_workflow_to_update(self):
        current = dict.fromkeys(SDK_WORKFLOWS, '10.0.404')
        require_coordinated_sdk(current, '10.0.404')
        cases = [(current, '10.0.405')]
        for name in SDK_WORKFLOWS:
            cases.extend([(dict(current, **{name: '10.0.405'}), '10.0.404'),
                          (dict(current, **{name: None}), '10.0.404')])
        for versions, image in cases:
            with self.subTest(versions=versions, image=image):
                with self.assertRaises(ValueError) as failure:
                    require_coordinated_sdk(versions, image)
                for name in SDK_WORKFLOWS:
                    self.assertIn('.github/workflows/' + name, str(failure.exception))

    def test_dependabot_matches_both_dockerfiles_with_hostless_names(self):
        # Dependabot stores registry separately from name (upstream issue #16574).
        # This checks our selectors against these manifests, not the hosted updater.
        config = yaml.safe_load((ROOT / '.github/dependabot.yml').read_text())
        docker = next(item for item in config['updates'] if item['package-ecosystem'] == 'docker')
        group = docker['groups']['dotnet-images']
        self.assertEqual('dependency-name', group['group-by'])
        self.assertEqual('weekly', docker['schedule']['interval'])
        self.assertEqual('dev', docker['target-branch'])
        discovered = set()
        for relative in ('Dockerfile', 'packaging/docker/Dockerfile.release'):
            path = Path(relative)
            directory = '/' if path.parent == Path('.') else '/' + path.parent.as_posix()
            self.assertIn(directory, docker['directories'])
            names = re.findall(r'^FROM mcr\.microsoft\.com/(dotnet/[^: ]+):',
                               (ROOT / relative).read_text(), re.M)
            self.assertTrue(names, relative)
            for name in names:
                with self.subTest(file=relative, dependency=name):
                    discovered.add((relative, name))
                    self.assertTrue(any(fnmatchcase(name, item['dependency-name']) for item in docker['allow']),
                                    f'Dependabot allow rules exclude {name}; omit the registry hostname')
                    self.assertTrue(any(fnmatchcase(name, pattern) for pattern in group['patterns']))
                    ignored = {kind for item in docker['ignore'] if fnmatchcase(name, item['dependency-name'])
                               for kind in item.get('update-types', [])}
                    self.assertEqual({'version-update:semver-major', 'version-update:semver-minor'}, ignored)
        self.assertEqual({('Dockerfile', 'dotnet/sdk'), ('Dockerfile', 'dotnet/aspnet'),
                          ('packaging/docker/Dockerfile.release', 'dotnet/aspnet')}, discovered)

    def test_runtime_image_can_advance_without_raising_host_floor(self):
        for version in ('10.0.15', '10.0.16'):
            with self.subTest(version=version):
                image = f'FROM mcr.microsoft.com/dotnet/aspnet:{version}-resolute@sha256:' + 'a' * 64
                self.assertEqual(version, serviced_image(image, 'aspnet', '10.0.15')[1])
        for version in ('10.0.14', '10.0.9', '10.0.16-preview.1', '11.0.0'):
            with self.subTest(version=version), self.assertRaises(ValueError):
                serviced_image(f'FROM mcr.microsoft.com/dotnet/aspnet:{version}-resolute@sha256:' + 'a' * 64,
                               'aspnet', '10.0.15')
        for suffix in ('', '@sha256:short', '@sha256:' + 'z' * 64):
            with self.subTest(suffix=suffix), self.assertRaises(ValueError):
                serviced_image('FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-resolute' + suffix,
                               'aspnet', '10.0.15')

    def test_private_source_lanes_monitor_canonical_and_keep_runtime_validation(self):
        workflow = yaml.load((ROOT / '.github/workflows/dotnet.yml').read_text(), Loader=yaml.BaseLoader)
        job = next(job for job in workflow['jobs'].values()
                   if job.get('name') == 'Build Ubuntu ${{ matrix.ubuntu }} source')
        steps = job['steps']
        setup = next(step for step in steps if step['name'] == 'Setup reviewed private SDK for source builds')
        self.assertEqual('${{ runner.temp }}/miningcore-dotnet', setup['env']['DOTNET_INSTALL_DIR'])
        sentinel = next(step for step in steps if 'check-canonical-servicing.sh' in step.get('run', ''))
        self.assertNotIn('if', sentinel)
        self.assertNotIn('continue-on-error', sentinel)
        self.assertTrue(any('test-ubuntu-source-build.sh' in step.get('run', '') for step in steps))
        self.assertIn('run-managed-dependency-smoke',
                      (ROOT / 'scripts/release/test-ubuntu-source-build.sh').read_text())

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
