// The view in the address: #runtime or #runtime/<environment> opens the runtime view, #cluster the cluster view (where
// the topology has a cluster), anything else the health view.
// The page replaces the hash as the viewer switches (no history entry per click) and follows a hash typed or linked.
let listener = null;

export function hash() {
  return decodeURIComponent(location.hash.replace(/^#/, ''));
}

export function setHash(value) {
  const target = value ? `#${value.split('/').map(encodeURIComponent).join('/')}` : location.pathname + location.search;
  history.replaceState(history.state, '', target);
}

// Calls OnHashChanged(hash) on the .NET object when the hash changes; returns the hash now.
export function subscribe(dotNetObject) {
  unsubscribe();
  listener = () => dotNetObject.invokeMethodAsync('OnHashChanged', hash());
  window.addEventListener('hashchange', listener);
  return hash();
}

export function unsubscribe() {
  if (listener) {
    window.removeEventListener('hashchange', listener);
    listener = null;
  }
}
