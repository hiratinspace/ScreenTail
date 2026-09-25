# Rotating the vault's master key

**What the vault is.** Integration credentials — a tenant's ConnectWise key, a Hudu key — are stored in
the backend's `integrations` table as ciphertext (ST-009). Each row's secret is sealed with its own data
key, and that data key is sealed with the deployment's master key, `Vault:MasterKey`. Only the process
that holds the master key can open a row, and only a provider worker ever asks it to; the API shows the
last four characters and nothing more.

**Why two layers.** Rotating the master key is then a rewrap of about forty bytes per row, with the
secret itself never decrypted into a new ciphertext, and a cloud KMS can take the outer layer over later
without the rows changing shape. Today the master key is configuration, because there is no cloud yet
(ST-007).

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
   Where the deployment reads configuration — user secrets in development, environment variables in a
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
   It prints how many rows changed hands and exits without listening. Run it again: it should print
   `rotated 0`. If it does not, something wrote a row under the previous key between the two runs, which
   means a process is still holding the old configuration — find it before going on.
5. **Drop the previous key.** Remove `Vault:PreviousMasterKey` and restart. Rows are now readable only
   under the new key; the old one can be destroyed.

A row whose `KeyId` matches neither configured key cannot be opened, and the service says so with the id
rather than sending a provider garbage. That is the state you are in if step 5 ran before step 4
finished; put the previous key back and repeat step 4.

## What this does not cover

- **The provider's own key.** Rotating our master key does not change the ConnectWise or Hudu
  credential itself. Rotating those is done at the provider and then stored here again with
  `PUT /v1/integrations/{provider}`.
- **Who may store a credential.** Any device of the tenant may, until ST-010 brings roles.

Exercised by `backend/tests/ScreenTail.Api.Tests/Vault/KeyRotationTests.cs`, which does steps 2 to 5 in
memory and checks that every row opens after and none before.
