# Maintainer sponsorship setup

Use GitHub Sponsors for optional one-time and recurring contributions. No separate
payment website is required. The repository's public support statement is
[SPONSORING.md](../SPONSORING.md); the MIT license remains unchanged.

At preparation of preview.17, `github.com/sponsors/ToolMaker` redirected to the ordinary
profile. A working sponsorship page has not been verified. Do not advertise payment
as available or add an active Sponsor button until enrollment is complete.

The maintainer must complete their own contact, bank and tax information, review the
terms, enable two-factor authentication and submit the profile for GitHub approval.
See [GitHub's account setup instructions](https://docs.github.com/en/sponsors/receiving-sponsorships-through-github-sponsors/setting-up-github-sponsors-for-your-personal-account).

Suggested profile introduction:

> I maintain InterfaceDB, an interface-first .NET object database. All implemented
> modules are MIT licensed. Sponsorship helps me maintain storage correctness,
> security, documentation and cross-platform tests. Everyone receives the same
> software; sponsoring is optional and is not a support contract.

Start with a small set of optional monthly contributions and a one-time option.
Recognition can be opt-in; do not promise paid features or guaranteed delivery dates.

Once the live profile is verified, add `.github/FUNDING.yml` with the approved account,
for example `github: [ToolMaker]`, and update SPONSORING.md and the package README links.
Keep the payment configuration out of runtime code.
