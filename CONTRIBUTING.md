# Contributing

Thanks for wanting to help. Issues and pull requests are welcome.

- **Bugs:** say what you asked the agent to do, what happened, and your Windows version.
  Deskweave keeps a log of every agent action in each workspace's `evidence` folder
  (Settings > General > Open Deskweave data); the relevant lines help a lot.
- **Changes:** keep them small and focused. Build with the .NET 10 SDK:
  `dotnet build app/Deskweave.csproj`.
- **Tests:** `ui-probe` checks the app's screens and behavior, `engine-probe` the workspace
  engine. Both open real windows, so run them on a machine you're not using at the same time,
  ideally with a second monitor.
