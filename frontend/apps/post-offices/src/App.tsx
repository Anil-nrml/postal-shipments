import { Route, Routes } from 'react-router-dom';
import PostOfficeListPage from './PostOfficeListPage';
import PostOfficeFormPage from './PostOfficeFormPage';

/** Mounted by the shell at /post-offices/*. */
export default function PostOfficesApp() {
  return (
    <Routes>
      <Route index element={<PostOfficeListPage />} />
      <Route path="new" element={<PostOfficeFormPage />} />
      <Route path=":id/edit" element={<PostOfficeFormPage />} />
    </Routes>
  );
}
