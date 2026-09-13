# Security Policy

## Scope

This policy applies to the BTCPool.co.uk Miningcore distribution
maintained at https://github.com/NINJAK1DD/miningcore.

Other Miningcore forks and upstream projects have their own maintainers
and reporting processes.

## Supported Versions

Security fixes target the latest stable release. Older releases are
not guaranteed to receive backported fixes.

Reports affecting the dev branch are also welcome. Development builds
and release candidates are intended for testing and evaluation.

## Reporting a Vulnerability

Please report suspected vulnerabilities through GitHub's private
vulnerability reporting feature:

1. Open this repository's Security tab.
2. Select Advisories.
3. Click Report a vulnerability.

Do not disclose exploit details in public issues, discussions, or pull
requests before coordinated disclosure.

Include, where possible:

- The affected release or commit.
- A description of the vulnerability and its potential impact.
- Reproduction steps or a minimal proof of concept.
- Relevant configuration and logs, with sensitive information removed.
- Any suggested mitigation or fix.

Never include wallet private keys, seed phrases, passwords, API tokens,
or other credentials.

## Encryption

GitHub's private vulnerability reporting feature is the primary reporting
channel. If you want to encrypt sensitive report details before submitting
them, use the maintainer's [public OpenPGP key](https://github.com/NINJAK1DD.gpg).

Verify the key's full primary fingerprint before encrypting:

```text
1CF5 C5D0 A8A1 E027 EEF4  30A6 D67E 44D9 32A2 727D
```

This key includes an encryption subkey. The primary key and encryption
subkey currently expire on 26 August 2029. Check that your OpenPGP software
considers the key valid before using it.

Submit the encrypted content through a private vulnerability report,
with a brief, non-sensitive summary so the report can be triaged.
Encryption is optional; you can also submit report details directly
through GitHub's private reporting form.

## Safe Testing

Use regtest, local deployments, or isolated environments you control.
Do not test against live pools or third-party systems without explicit
permission. Avoid tests that could disrupt mining, alter balances,
trigger payouts, or put funds at risk.

## Handling Reports

Reports are reviewed on a best-effort basis. Response and resolution
times depend on severity, complexity, and maintainer availability.

We will use the private report to discuss findings, coordinate fixes,
and agree on public disclosure where appropriate. Please keep details
private while that process is ongoing.
