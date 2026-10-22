export function createLatestLoad() {
  const slots = new Map();

  function getCurrent(slot) {
    return slots.get(String(slot || 'default')) || null;
  }

  return {
    begin(slot) {
      const key = String(slot || 'default');
      const previous = getCurrent(key);
      if (previous && previous.controller) {
        previous.controller.abort();
      }

      const token = Symbol(key);
      const controller = new AbortController();
      slots.set(key, { token, controller });

      return {
        token,
        signal: controller.signal,
        abort() {
          const current = getCurrent(key);
          if (current && current.token === token) {
            current.controller.abort();
            slots.delete(key);
          } else {
            controller.abort();
          }
        },
        isCurrent() {
          const current = getCurrent(key);
          return !!current && current.token === token;
        }
      };
    },
    finish(slot, token) {
      const key = String(slot || 'default');
      const current = getCurrent(key);
      if (current && current.token === token) {
        slots.delete(key);
      }
    }
  };
}
