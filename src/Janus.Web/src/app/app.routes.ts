import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    redirectTo: 'dashboard'
  },
  {
    path: 'dashboard',
    loadComponent: () => import('./features/dashboard/dashboard.component').then(m => m.DashboardComponent)
  },
  {
    path: 'cards',
    loadComponent: () => import('./features/cards/cards.component').then(m => m.CardsComponent)
  },
  {
    path: 'simulator',
    loadComponent: () => import('./features/simulator/simulator.component').then(m => m.SimulatorComponent)
  },
  {
    path: 'audit',
    loadComponent: () => import('./features/audit/audit.component').then(m => m.AuditComponent)
  },
  {
    path: '**',
    redirectTo: 'dashboard'
  }
];
