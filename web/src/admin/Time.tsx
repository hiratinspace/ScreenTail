import { absoluteTime, relativeTime } from './format';

/** A time in a list: relative, with the absolute time on hover and to assistive technology. */
export function Time({ at }: { at: string }) {
  const full = absoluteTime(at);
  return (
    <time dateTime={at} title={full}>
      {relativeTime(at)}
    </time>
  );
}
