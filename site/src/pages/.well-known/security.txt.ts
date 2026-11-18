import type { APIRoute } from 'astro';
import { site } from '../../site.config';

/** RFC 9116. Expires is a year from the build, so a site left unbuilt for a year says so. */
export const GET: APIRoute = () => {
  const expires = new Date(Date.now() + 365 * 24 * 3600 * 1000);
  expires.setUTCHours(0, 0, 0, 0);
  const body = [
    `Contact: mailto:${site.securityEmail}`,
    `Expires: ${expires.toISOString()}`,
    'Preferred-Languages: en',
    `Canonical: ${site.url}/.well-known/security.txt`,
    `Policy: ${site.url}/security/#report`,
    '',
  ].join('\n');
  return new Response(body, { headers: { 'Content-Type': 'text/plain; charset=utf-8' } });
};
