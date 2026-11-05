import { expect, expectAccessible, expectPageHeaders, test, world } from './fixtures.ts';

test.describe('the sign-in page', () => {
  test('says sign-in is not configured when the build has no client id, and loads nothing from Google', async ({ page }) => {
    const answer = await page.goto(world.unconfigured);
    expect(answer?.status()).toBe(200);
    expectPageHeaders(answer!.headers());

    await expect(page.getByRole('heading', { level: 1, name: 'Sign in to ScreenTail admin' })).toBeVisible();
    await expect(page.getByRole('status').filter({ hasText: 'Sign-in is not configured.' })).toBeVisible();
    await expect(page.getByRole('button', { name: /Sign in with Google/ })).toHaveCount(0);
    expect(await page.evaluate(() => 'google' in window)).toBe(false);

    await expectAccessible(page, 'sign-in, not configured');
  });

  test("draws Google's button from a fresh nonce when the build has a client id", async ({ page }) => {
    const nonce = page.waitForResponse((r) => r.url().endsWith('/v1/admin/session/nonce') && r.request().method() === 'POST');
    const answer = await page.goto(world.configured);
    expectPageHeaders(answer!.headers());

    expect((await nonce).status()).toBe(200);
    await expect(page.getByRole('button', { name: 'Sign in with Google (test)' })).toBeVisible();
    await expect(page.getByText('Sign-in is not configured.')).toHaveCount(0);

    await expectAccessible(page, 'sign-in, configured');
  });

  test('refuses, in words, a Google account that is not on the signup list', async ({ page, google }) => {
    google.account = { sub: `stranger-${world.run}`, email: `stranger-${world.run}@e2e.example`, name: 'Not Allowed' };
    await page.goto(world.configured);

    const refused = page.waitForResponse((r) => r.url().endsWith('/v1/admin/session') && r.request().method() === 'POST');
    await page.getByRole('button', { name: 'Sign in with Google (test)' }).click();

    expect((await refused).status()).toBe(403);
    await expect(page.getByRole('alert')).toContainText('not on the list allowed to set up ScreenTail');
    // A fresh button, for the next try: the spent nonce went with the old one.
    await expect(page.getByRole('button', { name: 'Sign in with Google (test)' })).toBeVisible();
  });

  test('the CSP watch hears a violation (the check of the check)', async ({ page, violations }) => {
    await page.goto(world.configured);
    await expect(page.getByRole('button', { name: 'Sign in with Google (test)' })).toBeVisible();

    // An inline script, which the page's policy forbids: if this is not heard, a clean run proves nothing.
    await page.evaluate(() => {
      const script = document.createElement('script');
      script.textContent = 'window.inlineRan = true;';
      document.head.appendChild(script);
    });

    await expect.poll(() => violations.length).toBeGreaterThan(0);
    expect(violations.join('\n')).toContain('script-src');
    expect(await page.evaluate(() => 'inlineRan' in window)).toBe(false);
    violations.length = 0;
  });
});
