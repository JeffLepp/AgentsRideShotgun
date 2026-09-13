# Chromium sandbox namespace policy

`playwright-v1.58.2.json` is an unchanged official Microsoft Playwright profile,
pinned to commit `ce480a952553175eae75342aad2c5e86cdf2cbba`. SHA-256:
`CC3E61CABDA6BBC1E53E54D27BA4D55A9D3BE829B6DD1A596F4A7B31B1CC7849`.
[Pinned source](https://github.com/microsoft/playwright/blob/ce480a952553175eae75342aad2c5e86cdf2cbba/utils/docker/seccomp_profile.json).

The profile follows [Playwright's non-root Docker browser guidance](https://playwright.dev/docs/docker).
It permits `clone`, `setns` and `unshare` so Chromium can create its own sandbox
namespaces. Removing that one first rule makes the parsed JSON exactly equal to
the historical Docker default linked by those docs, retained here as
`docker-d0d99b04-default.json` (SHA-256
`50FE67EDBA965C34491888675386F9034463A5AAC2017551C8F87F9723A4D274`).
[Pinned Docker baseline](https://github.com/docker-archive/engine/blob/d0d99b04cf6e00ed3fc27e81fc3d94e7eda70af3/profiles/seccomp/default.json).
This is a pinned historical policy, not a claim that it tracks the current
Docker daemon's default. Both upstream Apache-2.0 licenses are included.

Apply it to this container only with `--security-opt seccomp=<absolute-path>`.
Keep the non-root user, private shared-memory segment and resource limits. No
`--privileged`, `SYS_ADMIN`, unconfined seccomp, host display, host IPC or
`--no-sandbox` is required by this route. No host policy is edited.

Keep Chromium profiles and runtime sockets on Linux-backed storage. The supplied
container launcher uses a Docker named volume for `/data`. A Windows-backed bind
mount stalled browser initialization in the local fixture test; evidence export
may use a bind mount, but browser profiles should not. Actual gate outcomes and
measurement limits belong in `../../VALIDATION.md`.
