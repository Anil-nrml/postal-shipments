import { useState } from 'react';
import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import {
  AppBar,
  Box,
  Chip,
  Container,
  Divider,
  Drawer,
  IconButton,
  List,
  ListItemButton,
  ListItemIcon,
  ListItemText,
  Stack,
  Toolbar,
  Typography,
  useMediaQuery,
  useTheme,
} from '@mui/material';
import MenuIcon from '@mui/icons-material/Menu';
import DashboardOutlined from '@mui/icons-material/DashboardOutlined';
import LocalShippingOutlined from '@mui/icons-material/LocalShippingOutlined';
import TravelExploreOutlined from '@mui/icons-material/TravelExploreOutlined';
import StoreOutlined from '@mui/icons-material/StoreOutlined';
import ManageAccountsOutlined from '@mui/icons-material/ManageAccountsOutlined';
import Logout from '@mui/icons-material/Logout';
import { hasPermission, Permissions, type Permission, useLogout, useSession } from '@postal/shared';

const DRAWER_WIDTH = 240;

export const navItems: { to: string; label: string; permission: Permission; icon: React.ReactNode; end?: boolean }[] = [
  { to: '/', label: 'Dashboard', permission: Permissions.ShipmentsRead, icon: <DashboardOutlined />, end: true },
  { to: '/shipments', label: 'Shipments', permission: Permissions.ShipmentsRead, icon: <LocalShippingOutlined /> },
  { to: '/tracking', label: 'Tracking', permission: Permissions.ShipmentsRead, icon: <TravelExploreOutlined /> },
  { to: '/post-offices', label: 'Post offices', permission: Permissions.PostOfficesRead, icon: <StoreOutlined /> },
  { to: '/users', label: 'Users & roles', permission: Permissions.UsersManage, icon: <ManageAccountsOutlined /> },
];

export function Layout() {
  const theme = useTheme();
  const desktop = useMediaQuery(theme.breakpoints.up('md'));
  const [open, setOpen] = useState(false);
  const { data: user } = useSession();
  const logout = useLogout();
  const navigate = useNavigate();

  const nav = (
    <Box component="nav" aria-label="Main">
      <Toolbar />
      <List>
        {navItems
          .filter((item) => hasPermission(user, item.permission))
          .map((item) => (
            <ListItemButton
              key={item.to}
              component={NavLink}
              to={item.to}
              end={item.end}
              onClick={() => setOpen(false)}
              sx={{ '&.active': { bgcolor: 'action.selected', color: 'primary.main', '& svg': { color: 'primary.main' } } }}
            >
              <ListItemIcon>{item.icon}</ListItemIcon>
              <ListItemText primary={item.label} />
            </ListItemButton>
          ))}
      </List>
      <Divider />
    </Box>
  );

  return (
    <Box sx={{ display: 'flex', minHeight: '100vh', bgcolor: 'background.default' }}>
      <AppBar position="fixed" sx={{ zIndex: (t) => t.zIndex.drawer + 1 }}>
        <Toolbar>
          {!desktop && (
            <IconButton color="inherit" edge="start" onClick={() => setOpen(true)} aria-label="Open navigation" sx={{ mr: 1 }}>
              <MenuIcon />
            </IconButton>
          )}
          <Typography variant="h6" component="div" sx={{ flexGrow: 1 }}>
            Postal Shipments
          </Typography>
          {user && (
            <Stack direction="row" spacing={1.5} alignItems="center">
              <Box sx={{ textAlign: 'right', display: { xs: 'none', sm: 'block' } }}>
                <Typography variant="body2">{user.displayName}</Typography>
              </Box>
              <Chip size="small" label={user.role} sx={{ bgcolor: 'rgba(255,255,255,.18)', color: 'inherit' }} />
              <IconButton
                color="inherit"
                aria-label="Sign out"
                onClick={() => logout.mutate(undefined, { onSettled: () => navigate('/login') })}
              >
                <Logout />
              </IconButton>
            </Stack>
          )}
        </Toolbar>
      </AppBar>

      <Drawer
        variant={desktop ? 'permanent' : 'temporary'}
        open={desktop || open}
        onClose={() => setOpen(false)}
        sx={{ width: DRAWER_WIDTH, flexShrink: 0, '& .MuiDrawer-paper': { width: DRAWER_WIDTH, boxSizing: 'border-box' } }}
      >
        {nav}
      </Drawer>

      <Box component="main" sx={{ flexGrow: 1, minWidth: 0 }}>
        <Toolbar />
        <Container maxWidth="xl" sx={{ py: 3 }}>
          <Outlet />
        </Container>
      </Box>
    </Box>
  );
}
