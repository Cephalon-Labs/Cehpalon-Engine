# Engine tests

Future Engine suites will be split by component/provider/profile for targeted execution. Record exact Engine revision, dependencies, commands and proof scope. [PlatformQualification](PlatformQualification/README.md) is a bounded SDK/platform/third-party smoke executable with pinned packages and explicit Not run cases. Its passes do not establish Engine runtime feature maturity.

W0 correctness, tenant/policy, zero-activation and failure/recovery contracts are indexed in [sample profiles](../docs/w0-profiles.md). Implement each supported provider/profile suite from its canonical protocol; fixture passes cannot substitute for real-provider E2E.
