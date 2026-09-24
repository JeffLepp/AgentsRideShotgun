# Security

ARS lets coding agents run programs, click and type on a second Windows desktop. It is
not a sandbox: that desktop belongs to the same Windows user, with the same files, network and
permissions. Treat an agent connected to ARS the way you treat the agent itself.

What ARS does keep apart is input and windows. Programs started through its tools run on
the agent's desktop, and its clicks and keystrokes go there, not to yours. Sign-ins and other
security prompts are handed to you rather than typed by the agent.

## Reporting a problem

Please report security issues privately through
[GitHub's private vulnerability reporting](https://github.com/JeffLepp/AgentsRideShotgun/security/advisories/new)
rather than a public issue. You'll get a reply within a few days.
