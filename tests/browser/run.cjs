'use strict';

const { spawn } = require('node:child_process');
const path = require('node:path');

const repositoryRoot = path.resolve(__dirname, '..', '..');
const browserTests = [
  'FullyQualifiedName~NormalNavigationRefreshesPublishedEntryAndNestedModulesWithRealBrowserCaching',
  'FullyQualifiedName~PatronBrowserJourneyRunsOnKestrelWithRealSqlAndRecordingEmailTransport',
  'FullyQualifiedName~StaffBrowserJourneyRunsOnKestrelWithRealSqlScopeAndRecoveryBarriers',
  'FullyQualifiedName~LegacyRequestLinksReplaceDesktopAndMobileUrlsWithoutLeakingFailures',
  'FullyQualifiedName~PatronSubmitBrowserJourneyRunsWithIdentitySwitchAndDeferredResponses',
  'FullyQualifiedName~StaffRequestBrowserJourneyRunsAgainstRealSqlAndHttp',
  'FullyQualifiedName~SettingsBrowserJourneyRoundTripsEditorSnapshotsThroughRealSqlAndHttp',
  'FullyQualifiedName~LibrarySettingsProductionGetEditorPostPreservesOverrideAndOwnedMetadata',
  'FullyQualifiedName~ConfiguredPatronAcceptanceBrowserVerifiesRealFailuresCommitsAndRecovery',
  'FullyQualifiedName~OperationsBrowserLocalizesSqlUtcAndPreservesQueueIdentitiesAcrossScopes',
  'FullyQualifiedName~EmailDefaultsBrowserDisplaysFiveSeedsAndPreservesSavedTextAndSparseInheritance',
  'FullyQualifiedName~Campaign2SettingsEditorPreservesIdentityOwnershipAndConditionalFieldsThroughSqlAndReload',
  'FullyQualifiedName~StaffPresentationBrowserPreservesHeaderNavigationLookupOrderAndTabGeometry',
  'FullyQualifiedName~LeapLinksBrowserUsesPinnedNativeIdsAcrossStaffSurfaces',
  'FullyQualifiedName~SettingsEmailTestBrowserDispatchesAutomaticallyThroughRecordingPostmarkAndObservesSameReference',
  'FullyQualifiedName~StaffQueueAndDetailQuickCreateBrowserUsesFreshContextAndExplicitAllScope',
].join('|');

const child = spawn(
  process.platform === 'win32' ? 'dotnet.exe' : 'dotnet',
  [
    'test',
    '--project',
    path.join('tests', 'Asap.Tests', 'Asap.Tests.csproj'),
    '--configuration',
    'Release',
    '--no-build',
    '--no-restore',
    '--minimum-expected-tests',
    '17',
    '--filter',
    browserTests,
    ...(process.env.ASAP_CI_BROWSER_RESULTS ? [
      '--report-trx',
      '--report-trx-filename',
      path.basename(process.env.ASAP_CI_BROWSER_RESULTS),
      '--results-directory',
      path.dirname(process.env.ASAP_CI_BROWSER_RESULTS),
    ] : []),
  ],
  { cwd: repositoryRoot, stdio: 'inherit' },
);

child.once('error', (error) => {
  console.error(`Unable to start browser acceptance tests: ${error.message}`);
  process.exitCode = 1;
});

child.once('close', (code) => {
  process.exitCode = code === null ? 1 : code;
});
