# Configuring a Lucky Penny licence key

Miningcore uses AutoMapper 16 and does not currently use MediatR. This guide applies when an operator
has chosen a Lucky Penny licence for AutoMapper or a bundle that includes it. It does not determine
which licence terms apply to a deployment; review the upstream terms and obtain independent advice
when needed.

## Choose the environment variable

- Use `AUTOMAPPER_LICENSE_KEY` for an AutoMapper-specific key.
- Use `LUCKYPENNY_LICENSE_KEY` for a Lucky Penny bundle that includes AutoMapper, such as an
  AutoMapper and MediatR bundle.

AutoMapper checks the product-specific variable first. Configure only the applicable variable so an
old `AUTOMAPPER_LICENSE_KEY` cannot override a newer bundle key.

A key is a secret and must remain on one physical line. In the examples below, replace the complete
`PASTE_COMPLETE_KEY_HERE` text. Do not retain placeholder text, brackets or quotes around the key.

## systemd

Create a root-only environment file without placing the key in shell history:

```console
sudo install -m 0600 -o root -g root /dev/null /etc/miningcore-license.env
sudoedit /etc/miningcore-license.env
```

Add exactly one applicable line:

```text
LUCKYPENNY_LICENSE_KEY=PASTE_COMPLETE_KEY_HERE
```

For an AutoMapper-only licence, use `AUTOMAPPER_LICENSE_KEY` instead. Add the environment file to a
systemd drop-in rather than editing the packaged unit:

```console
sudo systemctl edit miningcore
```

```ini
[Service]
EnvironmentFile=/etc/miningcore-license.env
```

Reload, restart and verify without printing the key:

```console
sudo systemctl daemon-reload
sudo systemctl restart miningcore
sudo stat -c '%a %U:%G %n' /etc/miningcore-license.env
sudo journalctl -u miningcore --since "2 minutes ago" -o cat |
  grep -iE 'license|application started'
```

The file should report mode `600` and owner `root:root`. A successful startup logs that the key is
valid, its edition and expiry, followed by `Application started`. Avoid commands that print the
service's complete environment.

## Docker

Create the same root-only file on the Docker host, then add this option before the image name in the
existing `docker run` command:

```console
--env-file /etc/miningcore-license.env
```

For Compose, reference the protected host file with `env_file`. Do not copy the key into a Dockerfile,
Compose file, image layer or source checkout. The complete container setup remains in the
[release installation guide](releases.md#use-the-github-container-registry-image).

## Troubleshooting and rotation

- A Base64Url or JWT-header decoding error normally means the value contains placeholder characters,
  whitespace or a line break. Re-enter the key as one unquoted line; a JWT-shaped key has three
  dot-separated segments.
- A missing-key warning means the selected variable did not reach the process. Confirm the drop-in or
  container option uses the intended environment-file path.
- An invalid or expired message means the key reached AutoMapper but was not accepted. Confirm the
  product and expiry with Lucky Penny rather than suppressing the error.
- Miningcore continuing to start does not prove that the licence is valid; verify the licence log.

Replace an expired, revoked or disclosed key in the protected file, restart Miningcore and verify the
new validation message. Do not use a production key in tests or CI merely to suppress the warning.

See AutoMapper's official
[licence configuration documentation](https://docs.automapper.io/en/stable/License-configuration.html)
for variable precedence and validation behaviour.
