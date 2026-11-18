import type { APIRoute } from 'astro';
import { site } from '../site.config';

/** The pages worth finding. The form's answers and the 404 are left out (they are noindex too). */
const pages = ['/', '/security/', '/privacy/', '/accessibility/'];

export const GET: APIRoute = () => {
  const urls = pages.map((p) => `  <url><loc>${site.url}${p}</loc></url>`).join('\n');
  const body = `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n${urls}\n</urlset>\n`;
  return new Response(body, { headers: { 'Content-Type': 'application/xml; charset=utf-8' } });
};
