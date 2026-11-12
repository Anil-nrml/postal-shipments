import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Route, Routes } from 'react-router-dom';
import { Container } from '@mui/material';
import { AppProviders } from '@postal/shared';
import App from './App';
import DashboardPage from './DashboardPage';

// Standalone mode for local development of this MFE (sign in first via the shell or auth MFE).
createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AppProviders>
      <BrowserRouter>
        <Container sx={{ py: 3 }}>
          <Routes>
            <Route path="/shipments/*" element={<App />} />
            <Route path="*" element={<DashboardPage />} />
          </Routes>
        </Container>
      </BrowserRouter>
    </AppProviders>
  </StrictMode>,
);
