/**
 * The browser tests' world (ST-099 follow-up, ADR-0009 "Testing in a real browser"): the real backend in
 * Development against a real PostgreSQL, and the real built web admin served by `vite preview` with the
 * headers a deployment sends, twice: once built with a Google client id and once without.
 *
 * Google is never called. A key pair is made here for this run; the backend is handed its public half as
 * `Testing__GoogleSigningKeys` (refused outside Development, so it can never reach a deployment), and the
 * tests sign Google-shaped ID tokens with the private half. That half is written to a file only this user can
 * read, in a scratch folder removed at the end, and the workers are given its path: never the key itself, so
 * no environment (the browser's included, which Playwright starts with the workers' own) carries it.
 *
 * Needs `SCREENTAIL_E2E_POSTGRES` (an Npgsql connection string; the database in it is created and
 * migrated if missing) and a Release build of the API (`dotnet build
 * backend/src/ScreenTail.Api/ScreenTail.Api.csproj -c Release`, as CI runs it). docs/dev/web-admin.md, section 6.
 */
import { spawn, type ChildProcess } from 'node:child_process';
import { generateKeyPairSync, randomBytes } from 'node:crypto';
import { existsSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { build, preview, type PreviewServer } from 'vite';
import { allowed, env } from './env.ts';

const web = resolve(import.meta.dirname, '..');
const repo = resolve(web, '..');
const configFile = join(web, 'vite.config.ts');

async function freePort(): Promise<number> {
  return new Promise((done, fail) => {
    const server = createServer();
    server.once('error', fail);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      server.close(() => (typeof address === 'object' && address ? done(address.port) : fail(new Error('No port.'))));
    });
  });
}

/** Only what the dotnet host needs from this environment, so nothing of the runner's (the private key above all) reaches it. */
function hostEnvironment(extra: Record<string, string>): NodeJS.ProcessEnv {
  const passed: NodeJS.ProcessEnv = {};
  for (const name of ['PATH', 'HOME', 'USERPROFILE', 'TMPDIR', 'TEMP', 'TMP', 'DOTNET_ROOT', 'DOTNET_NOLOGO', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'LANG']) {
    if (process.env[name] !== undefined) {
      passed[name] = process.env[name];
    }
  }
  return { ...passed, ...extra };
}

function run(command: string, args: string[], environment: NodeJS.ProcessEnv): Promise<void> {
  return new Promise((done, fail) => {
    const child = spawn(command, args, { env: environment, stdio: ['ignore', 'inherit', 'inherit'] });
    child.once('error', fail);
    child.once('exit', (code) => (code === 0 ? done() : fail(new Error(`${command} ${args.join(' ')} exited with ${code}.`))));
  });
}

async function waitForHealth(url: string, child: ChildProcess, timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (child.exitCode !== null || child.signalCode !== null) {
      throw new Error(`The backend exited (${child.exitCode ?? child.signalCode}) before it answered.`);
    }
    try {
      const answer = await fetch(`${url}/health`);
      if (answer.ok) {
        return;
      }
    } catch {
      // Not listening yet.
    }
    await new Promise((r) => setTimeout(r, 200));
  }
  throw new Error(`The backend did not answer ${url}/health within ${timeoutMs / 1000} s.`);
}

export default async function globalSetup(): Promise<() => Promise<void>> {
  const postgres = process.env.SCREENTAIL_E2E_POSTGRES;
  if (!postgres) {
    throw new Error('Set SCREENTAIL_E2E_POSTGRES to an Npgsql connection string (docs/dev/web-admin.md, section 6).');
  }
  const dotnet = process.env.SCREENTAIL_E2E_DOTNET ?? 'dotnet';
  const dll = process.env.SCREENTAIL_E2E_API_DLL ?? join(repo, 'backend/src/ScreenTail.Api/bin/Release/net10.0/ScreenTail.Api.dll');
  if (!existsSync(dll)) {
    throw new Error(`No API build at ${dll}. Run: dotnet build backend/src/ScreenTail.Api/ScreenTail.Api.csproj -c Release`);
  }

  const runId = randomBytes(4).toString('hex');
  const keyId = `e2e-${runId}`;
  const clientId = `${runId}-screentail-e2e.apps.googleusercontent.com`;
  const downloadUrl = 'https://downloads.e2e.example/ScreenTail-Setup.exe';
  const { publicKey, privateKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
  const jwk = publicKey.export({ format: 'jwk' });
  const publicSet = JSON.stringify({ keys: [{ kty: 'RSA', kid: keyId, alg: 'RS256', use: 'sig', n: jwk.n, e: jwk.e }] });

  const scratch = mkdtempSync(join(tmpdir(), 'screentail-e2e-'));
  const stops: (() => Promise<void>)[] = [];
  const teardown = async () => {
    for (const stop of stops.reverse()) {
      await stop().catch(() => undefined);
    }
    rmSync(scratch, { recursive: true, force: true });
  };

  try {
    // The schema as a deployment applies it: the API's own --migrate, given the connection string alone.
    await run(dotnet, [dll, '--migrate'], hostEnvironment({ ConnectionStrings__Postgres: postgres }));

    const apiPort = await freePort();
    const api = `http://127.0.0.1:${apiPort}`;
    const settings: Record<string, string> = {
      ASPNETCORE_ENVIRONMENT: 'Development',
      ASPNETCORE_URLS: api,
      ConnectionStrings__Postgres: postgres,
      Jwt__SigningKey: randomBytes(48).toString('base64'),
      Vault__MasterKey: randomBytes(32).toString('base64'),
      Admin__GoogleClientId: clientId,
      Admin__NewTenantSeats: '3',
      Admin__ClientDownloadUrl: downloadUrl,
      Testing__GoogleSigningKeys: publicSet,
      // Every call comes from this machine's one address; the limit itself has its own backend tests.
      RateLimit__PermitLimit: '1000',
      // Pinned, so a developer's user secrets (read in Development) cannot change what is under test.
      Providers__Demo: 'false',
      ProviderNetwork__AllowPrivateAddresses: 'false',
      Summarization__Provider: 'gemini-flash',
      Summarization__ApiKey: '',
      Summarization__FallbackApiKey: '',
      Logging__LogLevel__Default: 'Warning',
    };
    allowed(runId).forEach((address, i) => (settings[`Admin__SignupAllowlist__${i}`] = address));

    const backend = spawn(dotnet, [dll], { env: hostEnvironment(settings), stdio: ['ignore', 'inherit', 'inherit'] });
    stops.push(
      () =>
        new Promise<void>((done) => {
          if (backend.exitCode !== null) {
            done();
            return;
          }
          backend.once('exit', () => done());
          backend.kill('SIGTERM');
        }),
    );
    await waitForHealth(api, backend, 60_000);

    // The page's /v1 goes to this backend through the preview server's proxy, as a deployment's proxy does.
    process.env.SCREENTAIL_API = api;
    const servers: Record<'configured' | 'unconfigured', string> = { configured: '', unconfigured: '' };
    for (const variant of ['configured', 'unconfigured'] as const) {
      const outDir = join(scratch, variant);
      // Set in the process, which Vite reads before any .env file, so a developer's web/.env.local is not what is tested.
      process.env.VITE_GOOGLE_CLIENT_ID = variant === 'configured' ? clientId : '';
      await build({ configFile, root: web, logLevel: 'warn', build: { outDir, emptyOutDir: true } });
      const port = await freePort();
      const server: PreviewServer = await preview({ configFile, root: web, logLevel: 'warn', build: { outDir }, preview: { port, strictPort: true, host: '127.0.0.1' } });
      stops.push(() => server.close());
      servers[variant] = `http://localhost:${port}`;
    }
    delete process.env.VITE_GOOGLE_CLIENT_ID;

    process.env[env.api] = api;
    process.env[env.configured] = servers.configured;
    process.env[env.unconfigured] = servers.unconfigured;
    process.env[env.clientId] = clientId;
    process.env[env.run] = runId;
    process.env[env.keyId] = keyId;
    const keyFile = join(scratch, 'signing-key.pem');
    writeFileSync(keyFile, privateKey.export({ format: 'pem', type: 'pkcs8' }), { mode: 0o600, flag: 'wx' });
    process.env[env.privateKeyFile] = keyFile;
    process.env[env.downloadUrl] = downloadUrl;
  } catch (e) {
    await teardown();
    throw e;
  }

  return teardown;
}
