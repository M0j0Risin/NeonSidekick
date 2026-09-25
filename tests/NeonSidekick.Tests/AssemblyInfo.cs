// DiagnosticLog is process-wide. The shell modes subscribe to it and forward every Warning to
// their own output, so a warning raised by a test class running in parallel would land inside
// another class's transcript (and a StringWriter is not thread-safe). Theme is process-wide too.
// Serial is no longer cheap (2026-09-24: ~5,400 tests, about 2.5 minutes on the CI runner, most of
// it ChatScreenTests' per-test setup), but determinism is still worth more than the speed-up; a
// parallel run would first need DiagnosticLog and Theme scoped per test.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
