# Security Policy

## Supported Code

Before the first stable release, security fixes are applied to the current default branch and then included in the next published preview. Older previews are superseded rather than maintained as parallel security-support lines unless this file explicitly says otherwise.

## Reporting A Vulnerability

Do not disclose a suspected vulnerability in a public issue, discussion, pull request, benchmark result, or sample data file.

Use GitHub private vulnerability reporting through the repository's **Security** tab when that option is available. Otherwise, contact the maintainer through an agreed private channel before sending sensitive details.

Include:

- affected commit or package version;
- operating system and .NET version;
- minimal reproduction steps;
- impact and required attacker access;
- whether encryption keys or persisted data may have been exposed;
- any suggested remediation, if known.

Do not include real secrets, customer data, or production encryption keys. Use generated test keys and synthetic payloads.

The project will acknowledge a complete report, reproduce it privately, prepare tests and a fix, and coordinate disclosure after users have a safe update path.
