import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { Container } from '@mui/material';
import { AppProviders } from '@postal/shared';
import App from './App';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AppProviders>
      <BrowserRouter>
        <Container sx={{ py: 3 }}>
          <Routes>
            <Route path="/tracking/*" element={<App />} />
            <Route path="*" element={<Navigate to="/tracking" replace />} />
          </Routes>
        </Container>
      </BrowserRouter>
    </AppProviders>
  </StrictMode>,
);
