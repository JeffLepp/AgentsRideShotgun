# Security

ARS lets coding agents run programs, click and type on a second Windows desktop. It is
not a sandbox: that desktop belongs to the same Windows user, with the same files, network and
permissions. Treat an agent connected to ARS the way you treat the agent itself.

What ARS does keep apart is input and windows. Programs started through its tools run on
the agent's desktop, and its clicks and keystrokes go there, not to yours. Sign-ins and other
security prompts are handed to you rather than typed by the agent.

## How it works

ARS is a native Windows app written in C# and WPF on .NET 10. Each workspace uses a Windows desktop object and a job object. Programs started through ARS tools, along with those tools' clicks and keystrokes, stay on that desktop; closing a workspace ends its owned processes. Claude Code and Codex connect through a small stdio MCP bridge that talks to the app over a local named pipe. An agent's first workspace tool call routes it to its project folder. The agent uses screenshots and Windows UI Automation, while Edge or Chrome runs with a separate workspace profile and a pipe-based browser control connection.

Workspaces share your Windows account and permissions. Programs launched outside ARS tools may still open on your desktop, and your AI provider may receive screenshots or tool results.

## Reporting a problem

Please report security issues privately through
[GitHub's private vulnerability reporting](https://github.com/JeffLepp/AgentsRideShotgun/security/advisories/new)
rather than a public issue. You'll get a reply within a few days.
