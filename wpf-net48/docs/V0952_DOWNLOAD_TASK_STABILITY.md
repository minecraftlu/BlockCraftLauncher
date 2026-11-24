# v0.9.52 RC1 download task stability

Based on the published v0.9.51 RC1 source (6ee54961c5eed7f21bc0031bbc528d274ebeb956).

- Retry ownership is acquired before changing observable properties; a reentrant retry cannot launch duplicate work.
- Progress callbacks are scoped to their attempt. Delayed progress cannot overwrite a cancelled, retried or completed task.
- Completion, cancellation and failure stop the progress animation and clear speed. Successful unknown-size tasks also finish at 100%.
- IsRunning, CanRetry and PercentLabel notify WPF bindings when their underlying state changes.
- Status/detail changes refresh download filters and counts through one deferred UI refresh.
- Existing layout, skins, authentication, download providers and updater behavior remain available.

Validation is provided by the Windows workflow `wpf-download-task-stability-check.yml`. It compiles and packages the WPF application and OneFile EXE, runs deterministic job lifecycle cases, real WPF filtering/binding checks, layout, skin and auth smoke checks, the existing offline downloader suite, and updater selftests. It uploads test artifacts only; it does not publish a Release or change the stable update channel.
