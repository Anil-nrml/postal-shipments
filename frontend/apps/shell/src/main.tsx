import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import { AppProviders } from '@postal/shared';
import { ShellRoutes } from './ShellRoutes';
import { remotes } from './remotes';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AppProviders>
      <BrowserRouter>
        <ShellRoutes remotes={remotes} />
      </BrowserRouter>
    </AppProviders>
  </StrictMode>,
);
