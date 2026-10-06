"""Static CI selection regressions; discovery/runtime and live evidence remain separate gates."""
import json
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]


def selected(class_name, selector):
    # This workflow uses only OR'd FullyQualifiedName substring selectors.
    return any(term.removeprefix('FullyQualifiedName~') in 'Bizigo.IntegrationTests.' + class_name
               or term.startswith('FullyQualifiedName~' + class_name + '.')
               for term in selector.split('|'))


class TopologyCiContractTests(unittest.TestCase):
    def setUp(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text()
        self.job = workflow.split('\n  topology-graph:\n', 1)[1].split('\n  sidecar:\n', 1)[0]

    def test_every_required_db_class_is_selected_in_the_same_single_trx(self):
        command = next(line for line in self.job.splitlines() if 'dotnet test tests/Bizigo.IntegrationTests' in line)
        selector = re.search(r"--filter '([^']+)'", command).group(1)
        verifier = next(line for line in self.job.splitlines() if 'otlp-verify-trx.py TestResults/topology-db.trx' in line)
        required = verifier.split('--require-class ', 1)[1].split()
        for cls in required:
            with self.subTest(cls=cls):
                self.assertTrue((ROOT / 'tests/Bizigo.IntegrationTests' / (cls + '.cs')).exists())
                self.assertTrue(selected(cls, selector), 'required class cannot execute under CI filter')
        self.assertTrue({'TopologyReplayProcessTests', 'TopologyArchiveProcessIntegrationTests',
                         'TopologyConflictIsolationIntegrationTests', 'TopologyReplayNegativeIntegrationTests',
                         'TopologyApiOracleIntegrationTests', 'TopologyHttpBudgetIntegrationTests',
                         'TopologyMutationOracleIntegrationTests',
                         'TopologyEdgeConcurrentOwnerOracleIntegrationTests', 'TopologyHistoricalDisplayOracleIntegrationTests',
                         'TopologyEdgeMigrationOracleIntegrationTests', 'SourceOwnershipHistoryIntegrationTests',
                         'TopologyEdgeDetailWindowOracleIntegrationTests', 'TopologyExactCapturedExpiryIntegrationTests',
                         'TopologyFinalEvidenceOracleIntegrationTests', 'TopologyFinalExpiryOracleIntegrationTests',
                         'TopologyFinalRouteAndAuditOracleIntegrationTests', 'TopologyFinalScopeOracleIntegrationTests',
                         'TopologyMappedSourceGroupOracleIntegrationTests', 'TopologyScopedArchitectureOracleTests',
                         'TopologyQueryIntegrationTests'}.issubset(required))
        self.assertIn('LogFileName=topology-db.trx', command)
        self.assertEqual(1, verifier.count('.trx'))

    def test_readiness_and_archive_regressions_execute_in_required_unit_trx(self):
        command = next(line for line in self.job.splitlines() if 'dotnet test tests/Bizigo.UnitTests' in line)
        selector = re.search(r"--filter '([^']+)'", command).group(1)
        verifier = next(line for line in self.job.splitlines() if 'otlp-verify-trx.py TestResults/topology-unit.trx' in line)
        required = verifier.split('--require-class ', 1)[1].split()
        critical = {'SignalReplayReadinessTests', 'SignalArchiveBoundaryTests', 'SignalDurabilityTests',
                    'LegacySignalReplayTests', 'TopologyGroupedAncestorTests', 'TopologyOutsideCountTests',
                    'TopologyConflictIsolationTests', 'TopologyProviderRemovalTests'}
        self.assertTrue(critical.issubset(required))
        self.assertFalse(selected('SignalReplayReadinessTests', 'FullyQualifiedName~Topology'))
        for cls in required:
            with self.subTest(cls=cls):
                self.assertTrue((ROOT / 'tests/Bizigo.UnitTests' / (cls + '.cs')).exists())
                self.assertTrue(selected(cls, selector), 'required unit class cannot execute under CI filter')
        self.assertIn('LogFileName=topology-unit.trx', command)
        self.assertEqual(1, verifier.count('.trx'))

    def test_historical_filter_omission_is_rejected(self):
        old = 'FullyQualifiedName~Telemetry|FullyQualifiedName~MetricTraceEvidence|FullyQualifiedName~GoldenReview'
        self.assertFalse(selected('TopologyReplayProcessTests', old))
        self.assertFalse(selected('TopologyArchiveProcessIntegrationTests', old))

    def test_process_matrix_cannot_silently_shrink(self):
        source = (ROOT / 'tests/Bizigo.IntegrationTests/TopologyReplayProcessTests.cs').read_text()
        cases = re.findall(r'\[InlineData\("([^"]+)", (true|false)\)\]', source)
        stages = {'after-wal-before-ack', 'archive-before-manifest',
                  'after-observed-db-before-publish', 'after-telemetry-db-before-checkpoint'}
        self.assertEqual({(stage, order) for stage in stages for order in ('true', 'false')}, set(cases))
        self.assertEqual(8, len(cases))
        self.assertIn('victim.Kill(entireProcessTree: true)', source)
        self.assertIn('Assert.NotEqual(victim.Id, recovery.Id)', source)

    def test_all_26_variants_have_execution_not_just_validation(self):
        plan = json.loads((ROOT / 'tools/topology-graph-mutations.json').read_text())
        mutations = plan['mutations']
        self.assertEqual(26, len({entry['id'] for entry in mutations}))
        self.assertEqual(23, sum(entry['project'] == 'UnitTests' for entry in mutations))
        self.assertEqual(3, sum(entry['project'] == 'IntegrationTests' for entry in mutations))
        self.assertIn('--mode unit --evidence-dir', self.job)
        self.assertIn('--mode db --evidence-dir', self.job)
        self.assertNotIn('--allow-partial', self.job)

    def test_two_fresh_nonce_runs_and_owned_cleanup_remain_wired(self):
        self.assertIn('--evidence-dir /tmp/topology-probe-one', self.job)
        self.assertIn('--evidence-dir /tmp/topology-probe-two', self.job)
        self.assertIn('--stop --session /tmp/topology-session/session.json', self.job)
        self.assertIn('trap ', self.job)
        self.assertIn('owned_pid_alive', self.job)
        self.assertIn('owned_container_alive', self.job)
        self.assertIn('if: always()', self.job)
        self.assertNotIn('docker system prune', self.job)


if __name__ == '__main__':
    unittest.main()
