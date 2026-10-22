# Security

This tool reads complete accounting data and handles a database connection string. Please report vulnerabilities privately through GitHub's "Report a vulnerability" button on this repository rather than opening a public issue.

When reporting a problem of any kind, never attach snapshots, manifests, raw XML, JSONL, logs containing company paths, or connection strings. Describe the failure with sanitized error output and aggregate counts; reproduce with synthetic data where possible.

Operational guidance:

- Keep snapshot directories on a local drive with private NTFS ACLs. Manifest hashes detect accidental corruption, not deliberate tampering.
- The installer stores the Postgres connection string with Windows DPAPI for the installing user. The loader reads it only from the process environment, never from arguments or files in the repository.
- Use `SSL Mode=Require` (or `VerifyFull`) unless the database is reachable only over a private, encrypted network you control.
- Grant the Postgres role only the privileges needed for its schema.
