import { Route, Routes } from 'react-router-dom';
import ShipmentListPage from './ShipmentListPage';
import ShipmentFormPage from './ShipmentFormPage';
import ShipmentDetailsPage from './ShipmentDetailsPage';

/** Mounted by the shell at /shipments/*. Routes are relative to that base. */
export default function ShipmentsApp() {
  return (
    <Routes>
      <Route index element={<ShipmentListPage />} />
      <Route path="new" element={<ShipmentFormPage />} />
      <Route path=":id" element={<ShipmentDetailsPage />} />
      <Route path=":id/edit" element={<ShipmentFormPage />} />
    </Routes>
  );
}
