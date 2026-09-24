# Security policy

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Use GitHub's
[private vulnerability reporting](https://github.com/chrisgrulau/jellyfin-ingest/security/advisories/new) instead.

## Secrets

This plugin needs no credentials of its own — it relies on the metadata providers configured in Jellyfin.
Nothing secret belongs in this repository: no API keys, tokens, server addresses or real library paths.
`.gitignore` blocks the common secret file patterns as a safety net, not as a substitute for care.

## File-system scope

The plugin moves and deletes files, so it only ever operates inside the watch folders, the configured library roots and
the quarantine folder, and it never deletes anything except expired quarantine entries.
