// Page Visibility for the dashboard: polling stops while the browser tab is hidden.
let listener = null;

export function isVisible() {
  return document.visibilityState !== 'hidden';
}

// Calls OnVisibilityChanged(visible) on the .NET object whenever the tab is hidden or shown; returns the state now.
export function subscribe(dotNetObject) {
  unsubscribe();
  listener = () => dotNetObject.invokeMethodAsync('OnVisibilityChanged', isVisible());
  document.addEventListener('visibilitychange', listener);
  return isVisible();
}

export function unsubscribe() {
  if (listener) {
    document.removeEventListener('visibilitychange', listener);
    listener = null;
  }
}
