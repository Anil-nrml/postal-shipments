import { Route, Routes } from 'react-router-dom';
import TrackingPage from './TrackingPage';

/** Mounted by the shell at /tracking/*; /tracking/:number deep-links straight to a shipment. */
export default function TrackingApp() {
  return (
    <Routes>
      <Route index element={<TrackingPage />} />
      <Route path=":number" element={<TrackingPage />} />
    </Routes>
  );
}
