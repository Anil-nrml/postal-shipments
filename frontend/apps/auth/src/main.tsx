import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Route, Routes } from 'react-router-dom';
import { AppProviders } from '@postal/shared';
import LoginPage from './LoginPage';
import UsersPage from './UsersPage';

// Standalone mode: lets this team develop and test the MFE without the shell.
createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AppProviders>
      <BrowserRouter>
        <Routes>
          <Route path="/users" element={<UsersPage />} />
          <Route path="*" element={<LoginPage />} />
        </Routes>
      </BrowserRouter>
    </AppProviders>
  </StrictMode>,
);
