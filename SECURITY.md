# Security

ARS lets coding agents run programs, click and type on a second Windows desktop. It is
not a sandbox: that desktop belongs to the same Windows user, with the same files, network and
permissions. Treat an agent connected to ARS the way you treat the agent itself.

What ARS does keep apart is input and windows. Programs started through its tools run on
the agent's desktop, and its clicks and keystrokes go there, not to yours. Sign-ins and other
security prompts are handed to you rather than typed by the agent.

## How it works

ARS is a native Windows app (C#, WPF, .NET 10). Each workspace is a Windows desktop object with a job object, so programs started through ARS tools, and their clicks and keystrokes, stay on that desktop, and closing the workspace ends them. Claude Code and Codex connect through a small stdio MCP bridge that talks to the app over a local named pipe. Agents see the workspace through screenshots and UI Automation.

**Separate browser profile** is off by default. With your browser's permission, ARS connects directly to your running Chromium browser and creates background tabs in its existing session. ARS reads only the browser's connection endpoint metadata, never its password or cookie databases, and does not copy your profile. Page text omits password-field values. ARS restricts its browser tools to tabs created by its connection and their popups; it does not adopt your existing tabs. Ending a workspace closes its own tabs and disconnects without stopping your browser. Browsing with your live sign-ins gives the agent access to those sites as you, so the browser requires you to allow the connection.

Turning **Separate browser profile** on runs Edge or Chrome in a profile kept in the workspace folder. That browser stores its own sign-ins; deleting a workspace removes that separate profile. ARS's screenshots and activity records can contain visible page content, and your AI provider may receive them.

Workspace programs run with your account's rights and never more. ARS does not run as administrator: a copy started with Run as administrator restarts as your normal user, because programs your agent starts would otherwise get administrator rights too. With UAC turned off, every program has full rights, ARS included.

Programs launched outside ARS tools may still open on your desktop, and your AI provider may receive screenshots or tool results.

## Reporting a problem

Please report security issues privately through
[GitHub's private vulnerability reporting](https://github.com/JeffLepp/AgentsRideShotgun/security/advisories/new)
rather than a public issue. You'll get a reply within a few days.
