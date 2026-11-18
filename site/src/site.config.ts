/**
 * The site's facts in one place. The domain and the contact addresses are not chosen yet (the owner
 * picks them; docs/dev/site.md). Until they are, the build uses the placeholders below and
 * scripts/check-dist.mjs refuses a production build that still has one.
 */
export const site = {
  name: 'ScreenTrail',
  /** The public address, with no trailing slash. Set SITE_URL when building for production. */
  url: process.env.SITE_URL ?? 'https://screentrail.example',
  /** Where pilot enquiries and security reports go. Set before the first production deploy. */
  contactEmail: process.env.SITE_CONTACT_EMAIL ?? 'hello@screentrail.example',
  securityEmail: process.env.SITE_SECURITY_EMAIL ?? 'security@screentrail.example',
  /** The public repository: the privacy pack and the threat model are read there. */
  repo: 'https://github.com/hiratinspace/ScreenTrail',
  description:
    'ScreenTrail drafts the ticket note while an MSP technician works a remote session, masks secrets on the PC, and publishes to ConnectWise and Hudu when the technician says so. In development, looking for pilot MSPs.',
} as const;

export const placeholderDomain = 'screentrail.example';
