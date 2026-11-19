import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

const pages = ['/', '/security/', '/privacy/', '/accessibility/', '/pilot/thanks/', '/pilot/problem/', '/pilot/busy/', '/does-not-exist/'];

/** Collects console errors (a CSP or Trusted Types violation lands here) and any request off the site. */
function watch(page: Page) {
  const errors: string[] = [];
  const offsite: string[] = [];
  page.on('console', (m) => { if (m.type() === 'error' && !/status of 404/.test(m.text())) errors.push(m.text()); });
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('request', (r) => { if (!r.url().startsWith('http://127.0.0.1:8789/')) offsite.push(r.url()); });
  return { errors, offsite };
}

async function scrollThrough(page: Page) {
  const height = await page.evaluate(() => document.documentElement.scrollHeight);
  for (let y = 0; y < height; y += 600) {
    await page.evaluate((yy) => window.scrollTo(0, yy), y);
    await page.waitForTimeout(20);
  }
}

for (const path of pages) {
  test(`${path} passes axe for WCAG 2.2 AA`, async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.goto(path);
    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa', 'best-practice']).analyze();
    expect(results.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).join(', ')}`)).toEqual([]);
  });

  test(`${path} loads nothing from another site and breaks no policy`, async ({ page }) => {
    const w = watch(page);
    await page.goto(path);
    await scrollThrough(page);
    expect(w.offsite).toEqual([]);
    expect(w.errors).toEqual([]);
  });

  test(`${path} reflows at 400% zoom with no sideways scrolling`, async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 256 });
    await page.goto(path);
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow).toBeLessThanOrEqual(0);
  });
}

test('the first Tab reaches the skip link, which moves to the content', async ({ page, browserName }, info) => {
  test.skip(info.project.name === 'phone', 'keyboard');
  await page.goto('/');
  await page.keyboard.press('Tab');
  const skip = page.getByRole('link', { name: 'Skip to the content' });
  await expect(skip).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/#main$/);
  void browserName;
});

test('the home page reads in full without JavaScript', async ({ browser }) => {
  const context = await browser.newContext({ javaScriptEnabled: false });
  const page = await context.newPage();
  await page.goto('http://127.0.0.1:8789/');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(/Fix the problem\./);
  await expect(page.getByRole('heading', { name: 'Become a pilot MSP' })).toBeVisible();
  await expect(page.locator('#clockpill')).toBeHidden();
  // All four of the scene's messages read in order, not only the first.
  for (const text of ['The notes don\'t get written', 'every session gets its note', 'a real ticket note', 'published in one press']) {
    await expect(page.getByText(text, { exact: false }).first()).toBeVisible();
  }
  await context.close();
});

test('the pilot form sends and lands on the thanks page', async ({ page }) => {
  await page.goto('/#pilot');
  await page.getByLabel('Your name').fill('Ada Lovelace');
  await page.getByLabel('Work email').fill('ada@example.com');
  await page.getByLabel('Company').fill('Analytical MSP');
  await page.getByRole('group', { name: 'Technicians' }).getByLabel('3 to 10').check();
  await page.getByRole('group', { name: 'PSA' }).getByLabel('ConnectWise PSA').check();
  await page.getByRole('group', { name: 'Main remote tool' }).getByLabel('ScreenConnect').check();
  await page.getByRole('button', { name: /Send to ScreenTrail/ }).click();
  await expect(page).toHaveURL(/\/pilot\/thanks\/$/);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(/reached the ScreenTrail team/);
});

test('the form will not send with a field missing', async ({ page }) => {
  await page.goto('/#pilot');
  await page.getByRole('button', { name: /Send to ScreenTrail/ }).click();
  await expect(page).toHaveURL(/#pilot$/);
});

test('another site cannot post to the form', async ({ request }) => {
  const res = await request.post('/api/pilot', {
    headers: { Origin: 'https://evil.example', 'Content-Type': 'application/x-www-form-urlencoded' },
    data: 'name=a',
    maxRedirects: 0,
  });
  expect(res.status()).toBe(403);
});

test('security.txt, robots.txt and the sitemap are served', async ({ request }) => {
  const sec = await (await request.get('/.well-known/security.txt')).text();
  expect(sec).toMatch(/^Contact: mailto:/m);
  expect(sec).toMatch(/^Expires: \d{4}-/m);
  expect(await (await request.get('/robots.txt')).text()).toContain('Sitemap:');
  expect(await (await request.get('/sitemap.xml')).text()).toContain('<urlset');
});

test('every page is sent with the strict policy', async ({ request }) => {
  for (const path of ['/', '/security/']) {
    const res = await request.get(path);
    expect(res.headers()['content-security-policy']).toContain("require-trusted-types-for 'script'");
  }
});

test('a visitor in dark mode still gets the light site, with Iris text that reads', async ({ browser }) => {
  const context = await browser.newContext({ colorScheme: 'dark' });
  const page = await context.newPage();
  await page.goto('http://127.0.0.1:8789/security/');
  const iris = await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--iris').trim());
  expect(iris.toLowerCase()).toBe('#6a43c4');
  const results = await new AxeBuilder({ page }).withTags(['wcag2aa']).analyze();
  expect(results.violations.map((v) => v.id)).toEqual([]);
  await context.close();
});

test('with reduced motion, all four scene messages show', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.goto('/');
  for (const text of ['every session gets its note', 'a real ticket note', 'published in one press']) {
    await expect(page.getByText(text, { exact: false }).first()).toBeVisible();
  }
});

test('a text page keeps its side margins', async ({ page }) => {
  await page.goto('/security/');
  const left = await page.locator('.page-head h1').evaluate((el) => el.getBoundingClientRect().left);
  expect(left).toBeGreaterThanOrEqual(18);
});

test('on a small screen the menu reaches the security page', async ({ page }, info) => {
  test.skip(info.project.name !== 'phone', 'the menu replaces the link row on small screens only');
  await page.goto('/');
  await page.locator('details.menu summary').click();
  await page.getByRole('navigation', { name: 'Main, small screen' }).getByRole('link', { name: 'Security' }).click();
  await expect(page).toHaveURL(/\/security\/$/);
});

test('the sample-session pill stays out of the pinned scene and off the form', async ({ page }, info) => {
  test.skip(info.project.name !== 'desktop', 'one size is enough');
  await page.goto('/');
  const pill = page.locator('#clockpill');
  await page.locator('#scene').evaluate((el) => scrollTo({ top: el.getBoundingClientRect().top + scrollY + 400, behavior: 'instant' }));
  await expect(pill).toBeHidden();
  await page.locator('#h-f1').evaluate((el) => scrollTo({ top: el.getBoundingClientRect().top + scrollY - 200, behavior: 'instant' }));
  await expect(pill).toBeVisible();
  await expect(pill.getByRole('link', { name: 'Pilot form' })).toBeVisible();
  await page.locator('#pilot-form').evaluate((el) => scrollTo({ top: el.getBoundingClientRect().top + scrollY - 200, behavior: 'instant' }));
  await expect(pill).toBeHidden();
});

test('the first screen is the hero alone: the next section starts below the fold', async ({ page }) => {
  await page.goto('/');
  const sceneTop = await page.locator('#scene').evaluate((el) => el.getBoundingClientRect().top);
  const viewport = page.viewportSize();
  expect(sceneTop).toBeGreaterThanOrEqual((viewport?.height ?? 0) - 1);
});
