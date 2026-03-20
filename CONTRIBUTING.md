# Contributing to SQL Log Shipping Service

Thanks for your interest in contributing! This document explains how to report issues, propose changes, run the project locally, and prepare a clean pull request.

**Code of Conduct**: Please follow the project's Code of Conduct: see `CODE_OF_CONDUCT.md`.

## Reporting Bugs

- Search existing issues first. If none match, open a new issue and include:
	- A short summary and steps to reproduce.
	- The application version
	- Any relevant screenshots

## Suggesting Enhancements

- For feature requests, open an issue describing the desired change, why it's needed, and any alternatives considered. 

## Branching & commits

- Create a descriptive branch from `main`
- Write atomic commits with clear messages. 

## Pull Request process

When your change is ready:

1. Ensure `dotnet build` and `dotnet test` pass locally.
2. Rebase the latest `main` and resolve conflicts locally.
3. Push your branch and open a pull request targeting `main`.
4. In the PR description:
	 - Reference the related issue (if any).
	 - Describe what changed and why.
	 - List any special testing or setup required to verify the change.
5. The maintainers will review; be prepared to make follow-up changes.


## Security issues

- For sensitive security issues, follow `SECURITY.md` rather than opening a public issue.

