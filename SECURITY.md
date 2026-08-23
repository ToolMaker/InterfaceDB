# Security Policy

## Supported Code

Before the first stable release, security fixes are applied to the current default branch only. After versioned packages exist, this file should list the supported release lines explicitly.

## Reporting A Vulnerability

Do not disclose a suspected vulnerability in a public issue, discussion, pull request, benchmark result, or sample data file.

Once the GitHub repository exists, enable GitHub private vulnerability reporting and submit the report through the repository's **Security** tab. Until then, send the maintainer a private report through an agreed private channel.

Include:

- affected commit or package version;
- operating system and .NET version;
- minimal reproduction steps;
- impact and required attacker access;
- whether encryption keys or persisted data may have been exposed;
- any suggested remediation, if known.

Do not include real secrets, customer data, or production encryption keys. Use generated test keys and synthetic payloads.

The project will acknowledge a complete report, reproduce it privately, prepare tests and a fix, and coordinate disclosure after users have a safe update path.
