# Choosing a workspace for your agent

ARS gives supported agents a separate Windows desktop on the computer you already use. Its value is access to your existing Windows environment plus a place for agent GUI work. Choose the execution environment according to what the task needs.

## Match the environment to the job

| Job requirement | Starting point | Reason |
| --- | --- | --- |
| A supported CLI, API or structured integration completes the job | Use that interface directly. | Avoids unnecessary GUI work and gives a result that is often easier to check. |
| The job stays entirely inside a web page | A browser automation tool. | It may already provide background tabs and structured controls. |
| The job depends on an installed Windows application or local project and needs GUI interaction | An ARS workspace, after checking that application's behavior. | The agent works in a separate desktop while retaining the local Windows context. |
| The job needs clean-state isolation from the Windows user | A properly configured sandbox or VM. | ARS shares the user's permissions; it is not that security boundary. |
| The job should run while your PC is unavailable, or needs separate compute | A cloud environment, suitable VM or another machine. | ARS uses the availability and resources of the current PC. |

These are starting points, not measured rankings. Task completion, manual help and application compatibility matter more than the tool category.

## Where local Windows context can matter

A task may depend on an existing project build, installed application, approved browser session or files already on the machine. For example, an agent can prepare a local change, inspect a supported native application and check an output file as part of one workflow. Native application testing is one example of this broader use.

Check the actual target first. Some applications hand off to an already-running instance; some controls reject background input; graphical capture also has application-specific limits. Follow an explicit refusal rather than assuming every Windows application will work. A successful task in one app does not certify another.

## A useful first task

Start with an application and outcome you already care about. Specify the exact action and a checkable result: a setting retained after reopening, an output file with expected contents, or a visible bug reproduced with evidence.

1. Install ARS through the [documented route](README.md#install-options), open it and connect the supported agent. Refresh tools or start a new agent session when needed.
2. Ask the agent to confirm which ARS tools it can actually call and to open the target through the supported workspace route.
3. Perform one bounded task and inspect the result independently. Record versions, any help required and failures.
4. If you are checking simultaneous use, actually continue your own activity during the task and observe whether it remains usable. Do not infer that result from a recording of the agent's window alone.

Prefer public sample files when sharing a demonstration. Screenshots, files and tool output may reach the selected AI provider. A local desktop does not imply local model inference or offline operation.

## Other tools can fit

Local and background agent workspaces are an established category. [Manus Desktop](https://manus.im/desktop) describes local files and an agent workspace on the user's computer; [Cua](https://cua.ai/pricing) provides software for computers users own or rent. [Playwright MCP](https://github.com/microsoft/playwright-mcp) focuses on browser interaction. [Windows Sandbox](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/) addresses a different isolation need.

Those links describe their vendors' offerings, not ARS comparison results. ARS is an option for people who want supported existing agents to operate a dedicated local Windows workspace. No universal superiority, unique local-computer claim or performance gain is established by this guide.

## What a workspace shares

The desktop uses the same Windows account and machine resources. It is a separate place for windows and input, not a second physical computer or an independent security sandbox. Ordinary shell launches are not universally redirected into it. Read [the security explanation](SECURITY.md) and [the ARS workflow](skills/ars/SKILL.md) before choosing it for sensitive work.
