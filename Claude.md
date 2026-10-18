# Claude Code Instructions — FxFixGateway.V2-DEV

## Git
- Claude may edit files, commit and push to `origin/master` when the user has asked for a change.
- One commit per change, with a clear message (a short subject line, then what and why). End commit messages with the
  attribution lines the session gives.
- No pull requests unless asked. Never force-push or rewrite history.
- Secrets (tokens, passwords, connection strings) never go into the repository.

## Communication
- Reply in Swedish; code, comments and UI text in English.
- No guessing: verify against the code, the FIX specifications and the data before changing behaviour. When something
  is unknown, say so.
- Exactness: no silent fallbacks; a message that cannot be handled is logged with the reason.

## Code
- Follow the surrounding code (layers Domain / Application / Infrastructure / UI, repositories with MySql.Data,
  hosted services, Serilog). Keep new files UTF-8.
- FIX handling on the QuickFIX thread must not block: parse, then hand the work to a background task.
- Database changes come as an SQL script under `docs/sql/` and are described in `Database.md`; the user runs the script.
- Build what can be built before committing (`dotnet build` of Domain, Application and Infrastructure; the UI needs
  local references that may be missing).
