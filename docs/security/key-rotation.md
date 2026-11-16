# Rotating the vault's master key

**What the vault is.** Integration credentials (a tenant's ConnectWise key, a Hudu key) are stored in
the backend's `integrations` table as ciphertext (ST-009). Each row's secret is sealed with its own data
key, and that data key is sealed with the deployment's master key, `Vault:MasterKey`. Only the process
that holds the master key can open a row, and only a provider worker ever asks it to; the API shows the
last four characters and nothing more.

**Why two layers.** Rotating the master key is then a rewrap of about forty bytes per row, with the
secret itself never decrypted into a new ciphertext, and a cloud KMS can take the outer layer over later
without the rows changing shape. Today the master key is configuration, because there is no cloud yet
(ST-007).

**Each secret is bound to its row.** Since 2026-11-15 the secret's layer is sealed with the tenant id,
the provider and the site's host and port as AES-GCM associated data, and the row records this in
`BindingVersion` (1). A bound row opens only for that tenant, provider and host: copied onto another
tenant's row, another provider or another host, it is refused with a message naming the row, and is
never tried any other way. Rows stored before then are marked 0 (legacy) by the migration and open
without a binding; each is sealed again in the bound form the first time the vault opens it, and these
also upgrade every legacy row at once:
```bash
dotnet run --project src/ScreenTail.Api -- --upgrade-vault-rows
```
It prints how many rows it upgraded; a second run prints `upgraded 0`. `--rotate-vault-keys` upgrades
legacy rows too. `BindingVersion` is a database column, so someone who can write the database can mark a
row legacy again: the guarantee covers rows written or upgraded since this change while legacy rows are
still accepted. Once `--upgrade-vault-rows` prints `upgraded 0`, set `Vault:AllowLegacyRows` to `false`
(default `true`) and restart: a row marked legacy is then refused rather than opened, which closes that
gap. Changing a site's host needs the credential stored again, which the owner's route asks for anyway.

**When to rotate.** On a schedule the security lead sets (quarterly is ordinary), and immediately if the
key may have been seen: a laptop with the secrets store lost, a config dump pasted somewhere, a person
with access leaving.

## The procedure

Every step is reversible until step 5, and a second run of step 4 is the check that the first finished.

1. **Make a new key.** Thirty-two random bytes, base64:
   ```bash
   openssl rand -base64 32
   ```
2. **Configure both keys.** The one in use becomes the previous key; the new one becomes current.
   Where the deployment reads configuration (user secrets in development, environment variables in a
   container:
   ```bash
   dotnet user-secrets set "Vault:PreviousMasterKey" "<the key in use until now>"
   dotnet user-secrets set "Vault:MasterKey" "<the new key>"
   ```
   ```bash
   export Vault__PreviousMasterKey='<the key in use until now>'
   export Vault__MasterKey='<the new key>'
   ```
3. **Restart the service with both.** From this moment new credentials are sealed under the new key,
   and rows still under the previous key open as before. Nothing is unreadable at any point.
4. **Rewrap.** Once, from a shell with the same configuration:
   ```bash
   dotnet run --project src/ScreenTail.Api -- --rotate-vault-keys
   ```
   It prints how many rows changed hands and exits without listening. "The same configuration" includes
   `Jwt:SigningKey`: the host checks it before any command runs, so without it the rewrap stops with a
   message naming the setting. Run it again: it should print
   `rotated 0`. If it does not, something wrote a row under the previous key between the two runs, which
   means a process is still holding the old configuration; find it before going on.
5. **Drop the previous key.** Remove `Vault:PreviousMasterKey` and restart. Rows are now readable only
   under the new key; the old one can be destroyed.

A row whose `KeyId` matches neither configured key cannot be opened, and the service says so with the id
rather than sending a provider garbage. That is the state you are in if step 5 ran before step 4
finished; put the previous key back and repeat step 4.

## What this does not cover

- **The provider's own key.** Rotating our master key does not change the ConnectWise or Hudu
  credential itself. Rotating those is done at the provider and then stored here again by the tenant's
  owner, in the web admin's Integrations page (`PUT /v1/admin/integrations/{provider}`).
- **Who may store a credential.** Only the tenant's owner, from the web admin (signed-in session and
  CSRF header). A technician's device lists what is connected, tests the stored credential and chooses
  company mappings while publishing; asked to store or remove a credential it is refused with
  `403 owner_only`. Every credential and mapping change is recorded (who, when, which provider, which
  companies, never a secret) and shown on the owner's Integrations page.

Exercised by `backend/tests/ScreenTail.Api.Tests/Vault/KeyRotationTests.cs`, which does steps 2 to 5 in
memory: a row under the previous key opens before the rewrap and a new one goes under the current key
(step 3), every row opens under the current key alone after it (steps 4 and 5), a row under a key the
deployment no longer holds is refused with that key's id, and legacy rows are sealed again bound. The
binding itself is exercised by `Vault/VaultBindingTests.cs`.
