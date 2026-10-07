// The traffic button: representative requests from this browser to the apps' public addresses, so the numbers on the
// runtime diagram move. A request is a plain GET in mode no-cors: the browser sends it like a link it follows and needs
// no CORS answer, and its response stays opaque (an answer counts as answered, a network failure as failed).
let run = null;

export function start(addresses, perSecond, seconds) {
  stop();
  const current = { sent: 0, answered: 0, failed: 0, done: false, stopped: false };
  run = current;
  const end = Date.now() + seconds * 1000;
  const pause = 1000 / perSecond;
  (async () => {
    let index = 0;
    while (!current.stopped && Date.now() < end) {
      const address = addresses[index++ % addresses.length];
      current.sent++;
      fetch(address, { mode: 'no-cors', cache: 'no-store', credentials: 'omit' })
        .then(() => current.answered++, () => current.failed++);
      await new Promise((resolve) => setTimeout(resolve, pause));
    }
    current.done = true;
  })();
}

// [sent, answered, failed, done (1 or 0)]; nothing sent and done before the first press.
export function status() {
  if (!run) return [0, 0, 0, 1];
  return [run.sent, run.answered, run.failed, run.done ? 1 : 0];
}

export function stop() {
  if (run) run.stopped = true;
}
