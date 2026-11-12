// Contracts of the modules each remote exposes. Every exposed module is a default-exported
// React component with no props, which keeps the shell <-> remote API small and versionable.
declare module 'auth/Login' {
  const Component: import('react').ComponentType;
  export default Component;
}
declare module 'auth/Users' {
  const Component: import('react').ComponentType;
  export default Component;
}
declare module 'shipments/App' {
  const Component: import('react').ComponentType;
  export default Component;
}
declare module 'shipments/Dashboard' {
  const Component: import('react').ComponentType;
  export default Component;
}
declare module 'postOffices/App' {
  const Component: import('react').ComponentType;
  export default Component;
}
declare module 'tracking/App' {
  const Component: import('react').ComponentType;
  export default Component;
}
