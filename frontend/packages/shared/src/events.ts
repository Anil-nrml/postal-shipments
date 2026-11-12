/**
 * Cross-micro-frontend events. Window CustomEvents keep MFEs decoupled: no MFE imports another,
 * they only agree on event names.
 */
export const SESSION_EXPIRED = 'postal:session-expired';
export const SESSION_CHANGED = 'postal:session-changed';

export function emitSessionExpired() {
  window.dispatchEvent(new CustomEvent(SESSION_EXPIRED));
}

export function emitSessionChanged() {
  window.dispatchEvent(new CustomEvent(SESSION_CHANGED));
}

export function onWindowEvent(name: string, handler: () => void): () => void {
  window.addEventListener(name, handler);
  return () => window.removeEventListener(name, handler);
}
