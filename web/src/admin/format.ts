/** Times as the spec shows them in lists: relative, with the absolute time on hover (Spec §4). */

const absolute = new Intl.DateTimeFormat('en', { dateStyle: 'medium', timeStyle: 'short' });

export function absoluteTime(iso: string): string {
  return absolute.format(new Date(iso));
}

export function relativeTime(iso: string, now: Date = new Date()): string {
  const seconds = Math.round((new Date(iso).getTime() - now.getTime()) / 1000);
  const future = seconds > 0;
  const span = Math.abs(seconds);
  let words: string;
  if (span < 60) {
    return future ? 'in under a minute' : 'just now';
  } else if (span < 3600) {
    words = `${Math.round(span / 60)} min`;
  } else if (span < 86400) {
    const hours = Math.round(span / 3600);
    words = `${hours} ${hours === 1 ? 'hour' : 'hours'}`;
  } else {
    const days = Math.round(span / 86400);
    words = `${days} ${days === 1 ? 'day' : 'days'}`;
  }
  return future ? `in ${words}` : `${words} ago`;
}

export function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : 'Something went wrong. Try again.';
}
