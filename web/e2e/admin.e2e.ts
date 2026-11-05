import { request as http } from '@playwright/test';
import { randomBytes } from 'node:crypto';
import { allowed } from './env.ts';
import { expect, expectAccessible, expectApiHeaders, sessionCookie, test, world } from './fixtures.ts';

const csrfHeader = 'X-ScreenTail-CSRF';

test('an owner signs up, invites a technician, sees the device, revokes it, sets the policy, and signs out', async ({ page, google, context }) => {
  const run = world.run;
  const owner = { sub: `owner-sub-${run}`, email: allowed(run)[0]!, name: 'Erin Owner' };
  const organisation = `E2E Organisation ${run}`;
  const technician = { email: `tech-${run}@e2e.example`, name: 'Tess Technician' };
  const deviceName = `E2E-PC-${run}`;
  google.account = owner;

  await test.step('first sign-in of an allowed account asks for the organisation, then makes it', async () => {
    await page.goto(world.configured);
    await page.getByRole('button', { name: 'Sign in with Google (test)' }).click();

    await expect(page.getByRole('heading', { level: 1, name: 'Set up your organisation' })).toBeVisible();
    await expect(page.getByText(owner.email)).toBeVisible();
    await expectAccessible(page, 'first run');

    // A validation error first: no name.
    await page.getByRole('button', { name: 'Create organisation' }).click();
    await expect(page.getByText("Enter your organisation's name.")).toBeVisible();

    const signup = page.waitForResponse((r) => r.url().endsWith('/v1/admin/signup'));
    await page.getByLabel('Organisation name').fill(organisation);
    await page.getByRole('button', { name: 'Create organisation' }).click();
    const answer = await signup;
    expect(answer.status()).toBe(200);
    expectApiHeaders(answer.headers());

    await expect(page.getByRole('heading', { level: 1, name: 'Technicians and seats' })).toBeVisible();
    await expect(page.getByText(organisation)).toBeVisible();
    await expect(page.locator('.seats')).toHaveText(/0 of 3\s+seats in use/);

    // Attributes picked out, never the whole cookie, so a failure does not print the session's value.
    const cookie = (await context.cookies()).find((c) => c.name === sessionCookie);
    expect(cookie !== undefined, 'the admin session cookie is set').toBe(true);
    expect({ httpOnly: cookie?.httpOnly, secure: cookie?.secure, sameSite: cookie?.sameSite, path: cookie?.path, domain: cookie?.domain }).toEqual({
      httpOnly: true,
      secure: true,
      sameSite: 'Strict',
      path: '/',
      domain: 'localhost',
    });
    expect(await page.evaluate((name) => document.cookie.includes(name), sessionCookie), 'script can read the session').toBe(false);
  });

  await test.step('the ID token the sign-in used cannot be used again', async () => {
    const replay = await page.request.post(`${world.configured}/v1/admin/signup`, {
      data: { credential: google.issued.at(-1), tenantName: 'Replayed' },
    });
    expect(replay.status()).toBe(401);
  });

  await test.step('a change without the CSRF header is refused, even with the cookie', async () => {
    const refused = await page.request.post(`${world.configured}/v1/admin/invites`, { data: { email: `nocsrf-${run}@e2e.example`, displayName: 'x' } });
    expect(refused.status()).toBe(403);
  });

  let code = '';
  await test.step('an invite: a validation error, then the code shown once, copied, and gone after Done', async () => {
    await page.getByLabel('Work email').fill('not an address');
    await page.getByRole('button', { name: 'Create invite' }).click();
    await expect(page.getByText("Enter the technician's work email address.")).toBeVisible();

    await page.getByLabel('Work email').fill(technician.email);
    await page.getByLabel('Name (optional)').fill(technician.name);
    const created = page.waitForResponse((r) => r.url().endsWith('/v1/admin/invites') && r.request().method() === 'POST');
    await page.getByRole('button', { name: 'Create invite' }).click();
    expect((await created).status()).toBe(201);

    await expect(page.getByText(`Invite code for ${technician.email}`)).toBeVisible();
    code = (await page.locator('.issued .code').innerText()).trim();
    expect(code).toMatch(/^[A-Z0-9]{5}(-[A-Z0-9]{5}){3}$/);
    await expect(page.getByText('This code is shown once.')).toBeVisible();
    await expectAccessible(page, 'technicians, with an invite code shown');

    await page.getByRole('button', { name: 'Copy code' }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Code copied.' })).toBeVisible();
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(code);

    await page.getByRole('button', { name: 'Done' }).click();
    await expect(page.locator('.issued')).toHaveCount(0);
    expect(await page.content(), 'the code after Done').not.toContain(code);
    await expect(page.getByRole('row', { name: new RegExp(technician.email) })).toContainText('Waiting for activation');

    // Nothing the page reads again carries it, and a reload does not bring it back.
    const list = page.waitForResponse((r) => r.url().endsWith('/v1/admin/technicians'));
    await page.reload();
    expect(await (await list).text(), 'the technicians list').not.toContain(code);
    await expect(page.getByRole('heading', { level: 1, name: 'Technicians and seats' })).toBeVisible();
    expect(await page.content(), 'the code after a reload').not.toContain(code);
  });

  // A credential-shaped value, made at run time: never a literal in the repository.
  const secret = `${randomBytes(18).toString('base64url')}Qzxw`;
  await test.step('the technician activates a device with the code, and it connects ConnectWise', async () => {
    const device = await http.newContext({ baseURL: world.api });
    try {
      const activated = await device.post('/v1/devices/activate', { data: { code, deviceName } });
      expect(activated.status()).toBe(200);
      const { accessToken } = (await activated.json()) as { accessToken: string };
      const stored = await device.put('/v1/integrations/connectwise', {
        headers: { Authorization: `Bearer ${accessToken}` },
        data: { siteUrl: `https://psa-${run}.e2e.example`, secret },
      });
      expect(stored.status()).toBe(204);
    } finally {
      await device.dispose();
    }

    await page.getByRole('button', { name: 'Refresh' }).click();
    const row = page.getByRole('row', { name: new RegExp(deviceName) });
    await expect(row).toContainText('Active');
    await expect(page.locator('.seats')).toHaveText(/1 of 3\s+seats in use/);
    await expect(page.getByRole('row', { name: new RegExp(technician.email) }).last()).toBeVisible();
  });

  await test.step('revoking needs the second click', async () => {
    const row = page.getByRole('row', { name: new RegExp(deviceName) });
    let revokes = 0;
    page.on('request', (r) => {
      if (r.url().includes('/revoke')) {
        revokes++;
      }
    });

    await row.getByRole('button', { name: `Revoke ${deviceName} (${technician.name})` }).click();
    const confirm = row.getByRole('button', { name: `Confirm revoke ${deviceName}` });
    await expect(confirm).toBeVisible();
    await expect(row).toContainText('Active');
    expect(revokes, 'requests after the first click').toBe(0);
    await expectAccessible(page, 'technicians, revoke confirmation');

    // Cancel takes it back without revoking.
    await row.getByRole('button', { name: 'Cancel' }).click();
    await expect(confirm).toHaveCount(0);
    expect(revokes).toBe(0);

    await row.getByRole('button', { name: `Revoke ${deviceName} (${technician.name})` }).click();
    await confirm.click();
    await expect(row).toContainText('Revoked');
    expect(revokes).toBe(1);
    await expect(page.locator('.seats')).toHaveText(/0 of 3\s+seats in use/);
  });

  await test.step('policy: a validation error, a save, and every-window capture only once confirmed (INV-5)', async () => {
    await page.getByRole('link', { name: 'Policy' }).click();
    await expect(page.getByRole('heading', { level: 1, name: 'Policy' })).toBeVisible();
    await expect(page.getByText('default (never set)')).toBeVisible();
    await expectAccessible(page, 'policy');

    let puts = 0;
    page.on('request', (r) => {
      if (r.url().endsWith('/v1/admin/policy') && r.method() === 'PUT') {
        puts++;
      }
    });

    const retention = page.getByLabel('Keep raw session data for (days)');
    await retention.fill('0');
    await expect(page.getByText('Enter a whole number of days from 1 to 365.')).toBeVisible();
    await page.getByRole('button', { name: 'Save policy' }).click();
    await expect(page.getByRole('alert')).toHaveText('Fix the fields marked below, then save again.');
    expect(puts, 'a save with a bad field').toBe(0);

    await retention.fill('14');
    await page.getByLabel('Lock this setting so technicians cannot change it').check();
    const saved = page.waitForResponse((r) => r.url().endsWith('/v1/admin/policy') && r.request().method() === 'PUT');
    await page.getByRole('button', { name: 'Save policy' }).click();
    expect((await saved).status()).toBe(200);
    await expect(page.getByRole('status').filter({ hasText: /Policy saved as version/ })).toBeVisible();

    await page.getByLabel('Every window during a session').check();
    const confirmation = page.getByLabel("I understand that every window on a technician's screen can be captured during a session");
    await expect(confirmation).toBeVisible();
    await expectAccessible(page, 'policy, every-window confirmation');
    await page.getByRole('button', { name: 'Save policy' }).click();
    await expect(page.getByRole('alert')).toHaveText('Fix the fields marked below, then save again.');
    expect(puts, 'every window without the confirmation').toBe(1);

    // The backend holds the line too, whatever a page sends.
    const csrf = ((await (await page.request.get(`${world.configured}/v1/admin/session`)).json()) as { csrf: string }).csrf;
    const current = (await (await page.request.get(`${world.configured}/v1/admin/policy`)).json()) as Record<string, unknown>;
    const unconfirmed = await page.request.put(`${world.configured}/v1/admin/policy`, {
      headers: { [csrfHeader]: csrf },
      data: { ...current, version: undefined, captureAllWindows: true, confirmAllWindows: false },
    });
    expect(unconfirmed.status()).toBe(400);
    expect(((await unconfirmed.json()) as { error: string }).error).toBe('all_windows_unconfirmed');

    await confirmation.check();
    const allWindows = page.waitForResponse((r) => r.url().endsWith('/v1/admin/policy') && r.request().method() === 'PUT');
    await page.getByRole('button', { name: 'Save policy' }).click();
    expect((await allWindows).status()).toBe(200);
    await expect(page.getByRole('status').filter({ hasText: /Policy saved as version/ })).toBeVisible();

    const policy = (await (await page.request.get(`${world.configured}/v1/admin/policy`)).json()) as { retentionDays: number; localOnlyLocked: boolean; captureAllWindows: boolean };
    expect(policy).toMatchObject({ retentionDays: 14, localOnlyLocked: true, captureAllWindows: true });
  });

  await test.step('integrations: connected, and no credential anywhere on the page or in what it read', async () => {
    const read = page.waitForResponse((r) => r.url().endsWith('/v1/admin/integrations'));
    await page.getByRole('link', { name: 'Integrations' }).click();
    const body = await (await read).text();
    const connectWise = page.getByRole('region', { name: 'ConnectWise Manage' });
    await expect(connectWise).toContainText('Connected');
    await expect(connectWise).toContainText(`https://psa-${run}.e2e.example`);
    await expect(page.getByRole('region', { name: 'Hudu' })).toContainText('Not connected');

    for (const piece of [secret, secret.slice(-4)]) {
      expect(body, 'the integrations answer').not.toContain(piece);
      expect(await page.content(), 'the integrations page').not.toContain(piece);
    }
    await expectAccessible(page, 'integrations');
  });

  await test.step('download: the deployment link', async () => {
    await page.getByRole('link', { name: 'Download client' }).click();
    const link = page.getByRole('link', { name: 'Download ScreenTail for Windows' });
    await expect(link).toHaveAttribute('href', world.downloadUrl);
    await expectAccessible(page, 'download client');
  });

  await test.step('sign-out ends the session, and a copy of the old cookie stops working', async () => {
    const cookie = (await context.cookies()).find((c) => c.name === sessionCookie)!;
    const copy = await http.newContext({ baseURL: world.configured, extraHTTPHeaders: { Cookie: `${cookie.name}=${cookie.value}` } });
    try {
      // The control: the copy works while the session does.
      expect((await copy.get('/v1/admin/session')).status()).toBe(200);

      await page.getByRole('button', { name: 'Sign out' }).click();
      await expect(page.getByRole('heading', { level: 1, name: 'Sign in to ScreenTail admin' })).toBeVisible();
      expect((await context.cookies()).some((c) => c.name === sessionCookie), 'the cookie still in this browser').toBe(false);

      expect((await copy.get('/v1/admin/session')).status()).toBe(401);
      expect((await copy.get('/v1/admin/technicians')).status()).toBe(401);
    } finally {
      await copy.dispose();
    }
  });

  await test.step('the same owner signs in again straight to the shell, at the section the address names', async () => {
    await page.getByRole('button', { name: 'Sign in with Google (test)' }).click();
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();
    await expect(page.getByRole('heading', { level: 1, name: 'Download client' })).toBeVisible();
    await expect(page.getByText(organisation)).toBeVisible();
  });
});
